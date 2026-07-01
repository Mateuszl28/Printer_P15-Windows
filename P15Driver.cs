using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace P15Printer;

/// <summary>
/// Driver for the Marklife / Pristar "P15 Mini" Bluetooth LE label printer.
///
/// Protocol (reverse-engineered, see tomLadder/thermoprint REVERSE_ENGINEERING.md):
///   Service   0000ff00-0000-1000-8000-00805f9b34fb
///   RX/Notify 0000ff01-...   printer -> host status bytes
///   TX/Write  0000ff02-...   host -> printer command + raster stream
///   CX/Ctrl   0000ff03-...   printer -> host flow-control credits
///
/// The printer paces the host with a credit scheme over the CX characteristic:
///   [0x01, n]            -> grants 'n' write credits (1 credit == 1 packet)
///   [0x02, lo, hi]       -> advertises MTU (little-endian)
/// Each on-air packet is capped at 95 bytes regardless of the negotiated MTU.
/// </summary>
public sealed class P15Driver : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = Guid.Parse("0000ff00-0000-1000-8000-00805f9b34fb");
    public static readonly Guid RxUuid      = Guid.Parse("0000ff01-0000-1000-8000-00805f9b34fb");
    public static readonly Guid TxUuid      = Guid.Parse("0000ff02-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CxUuid      = Guid.Parse("0000ff03-0000-1000-8000-00805f9b34fb");

    private const int PacketSize = 95; // hard cap imposed by the P15 firmware

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattCharacteristic? _tx;
    private GattCharacteristic? _rx;
    private GattCharacteristic? _cx;

    // Credit-based flow control. Seeded with a small budget so the very first
    // packets can flow before the printer issues its first credit grant; the
    // semaphore is then topped up from CX notifications.
    private readonly SemaphoreSlim _credits = new(initialCount: 8, maxCount: 1024);

    /// <summary>Raised for every status byte pair the printer reports on RX.</summary>
    public event Action<P15Status>? StatusReported;

    /// <summary>
    /// Finds the printer by its BLE printer service (not by name — these printers
    /// advertise under varied names like "Marklife"/"Pristar"/digits) and connects.
    /// When several devices expose the service, a non-null <paramref name="nameHint"/>
    /// is used only to break ties.
    /// </summary>
    public static async Task<P15Driver> ConnectAsync(string? nameHint = null, int timeoutMs = 15000)
    {
        var devices = await EnumerateBleAsync(Math.Min(timeoutMs, 8000));
        if (devices.Count == 0)
            throw new InvalidOperationException(
                "No BLE devices found. Pair the printer under Settings → Bluetooth & devices → Add device, " +
                "and make sure no other app (e.g. the phone) is connected to it.");

        // If a name hint is given, restrict to matching devices so we never pair
        // unrelated peripherals. Fall back to probing everything only when the
        // hint matches nothing.
        IEnumerable<KeyValuePair<string, string>> candidates = devices;
        if (nameHint is not null)
        {
            var matches = devices
                .Where(d => d.Value.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0) candidates = matches;
        }

        Exception? lastError = null;
        foreach (var (id, name) in candidates)
        {
            try
            {
                var driver = new P15Driver();
                await driver.OpenAsync(id);   // throws if the service is absent
                return driver;
            }
            catch (Exception ex)
            {
                lastError = ex;   // keep the detailed reason from the best candidate
            }
        }

        throw new InvalidOperationException(
            $"Found {devices.Count} BLE device(s) but could not open the P15. " +
            $"Last reason: {lastError?.Message ?? "unknown"}", lastError);
    }

    /// <summary>
    /// Enumerates BLE devices from two sources: paired/associated endpoints
    /// (via DeviceInformation) and in-range advertisers (via the advertisement
    /// watcher, so unpaired printers are still discoverable).
    /// Returns a map of WinRT device-id → friendly name.
    /// </summary>
    private static async Task<Dictionary<string, string>> EnumerateBleAsync(int durationMs)
    {
        var found = new Dictionary<string, string>();

        // Source 1: associated endpoints (covers paired devices in any state).
        try
        {
            var infos = await DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelector(), null, DeviceInformationKind.AssociationEndpoint);
            foreach (var info in infos)
                found[info.Id] = string.IsNullOrWhiteSpace(info.Name) ? "(no name)" : info.Name;
        }
        catch { /* ignore enumeration failures */ }

        // Source 2: live advertisements (covers in-range, not-yet-paired devices).
        // The Received handler fires on background threads, so we only collect raw
        // addresses here (synchronously, under a lock) and resolve names afterwards.
        var addresses = new HashSet<ulong>();
        var gate = new object();

        var adWatcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
        {
            ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Active,
        };
        adWatcher.Received += (_, e) =>
        {
            lock (gate) addresses.Add(e.BluetoothAddress);
        };
        adWatcher.Start();
        await Task.Delay(durationMs);
        adWatcher.Stop();

        ulong[] snapshot;
        lock (gate) snapshot = addresses.ToArray();

        foreach (var addr in snapshot)
        {
            try
            {
                using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                if (dev is not null && !found.ContainsKey(dev.DeviceId))
                    found[dev.DeviceId] = string.IsNullOrWhiteSpace(dev.Name) ? "(no name)" : dev.Name;
            }
            catch { /* unreachable advertiser — skip */ }
        }

        return found;
    }

    /// <summary>A Bluetooth LE device discovered during a scan.</summary>
    public readonly record struct ScanResult(string Name, string Id, bool HasPrinterService);

    /// <summary>
    /// Enumerates nearby/paired BLE devices for diagnostics. Marks which ones
    /// actually expose the P15 printer service — useful to confirm the printer
    /// is reachable over BLE regardless of the (irrelevant) Windows printer entry.
    /// </summary>
    public static async Task<IReadOnlyList<ScanResult>> ScanAsync(int durationMs = 6000)
    {
        var found = await EnumerateBleAsync(durationMs);

        var results = new List<ScanResult>();
        foreach (var (id, name) in found)
        {
            bool hasService = false;
            try
            {
                using var dev = await BluetoothLEDevice.FromIdAsync(id);
                if (dev is not null)
                {
                    var svc = await dev.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
                    hasService = svc.Status == GattCommunicationStatus.Success && svc.Services.Count > 0;
                }
            }
            catch { /* unreachable device — leave hasService false */ }

            results.Add(new ScanResult(name, id, hasService));
        }
        return results;
    }

    /// <summary>Connects to a known device by its WinRT device id (DeviceInformation.Id).</summary>
    public static async Task<P15Driver> ConnectByIdAsync(string deviceId)
    {
        var driver = new P15Driver();
        await driver.OpenAsync(deviceId);
        return driver;
    }

    private async Task OpenAsync(string deviceId)
    {
        _device = await BluetoothLEDevice.FromIdAsync(deviceId)
                  ?? throw new InvalidOperationException("Failed to open BLE device.");

        // Pair if needed (already-paired is fine).
        string pairInfo = await EnsurePairedAsync(_device);

        // 0x80070016 (ERROR_BAD_COMMAND) on GetGattServices means the LE link
        // isn't actually up yet. Force and hold a connection with a GattSession
        // (MaintainConnection = true), wait for it to connect, then query.
        // On a cold start (printer just woken) the link can take several seconds,
        // so wait generously — this makes the first `dotnet run` succeed instead of
        // needing 2-3 retries.
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
        _session.MaintainConnection = true;

        for (int i = 0; i < 60 && _device.ConnectionStatus != BluetoothConnectionStatus.Connected; i++)
            await Task.Delay(250);   // up to ~15 s for the link to come up

        var svcResult = await TryGetServiceAsync(attempts: 16);

        // Last resort: re-pair then try once more.
        if (NoService(svcResult) && pairInfo != "already-paired")
            svcResult = await TryGetServiceAsync(attempts: 4);

        if (NoService(svcResult))
            throw new InvalidOperationException(
                $"P15 service not reachable (gatt={svcResult?.Status.ToString() ?? "null"}, " +
                $"conn={_device.ConnectionStatus}, pairing={pairInfo}" +
                (_lastGattError is null ? "" : $", lastGattError={_lastGattError}") + "). " +
                "Make sure the printer is on, awake, and not connected to another app (e.g. phone).");

        var service = svcResult!.Services[0];
        _tx = await GetCharacteristicAsync(service, TxUuid);
        _rx = await GetCharacteristicAsync(service, RxUuid);
        _cx = await GetCharacteristicAsync(service, CxUuid);

        // Subscribe to status (RX) and flow-control credits (CX).
        _rx.ValueChanged += OnRxChanged;
        await EnableNotificationsAsync(_rx);

        _cx.ValueChanged += OnCxChanged;
        await EnableNotificationsAsync(_cx);
    }

    private static bool NoService(GattDeviceServicesResult? r) =>
        r is null || r.Status != GattCommunicationStatus.Success || r.Services.Count == 0;

    private string? _lastGattError;

    private async Task<GattDeviceServicesResult?> TryGetServiceAsync(int attempts)
    {
        GattDeviceServicesResult? result = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                // On a not-yet-connected link this can throw COMException 0x80070016
                // (ERROR_BAD_COMMAND) instead of returning a status — swallow it so
                // the caller can keep retrying while the GattSession brings the link up.
                // Alternate Uncached/Cached: once paired, Cached often succeeds where
                // Uncached still reports BAD_COMMAND.
                var mode = (i % 2 == 0) ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
                result = await _device!.GetGattServicesForUuidAsync(ServiceUuid, mode);
                if (!NoService(result)) return result;
            }
            catch (Exception ex)
            {
                _lastGattError = $"0x{ex.HResult:X8} {ex.Message.Trim()}";
                result = null;
            }
            await Task.Delay(600);
        }
        return result;
    }

    /// <summary>
    /// Pairs the device using "Just Works" custom pairing (no PIN) if it isn't
    /// already paired. Safe to call when already paired (no-op).
    /// </summary>
    private static async Task<string> EnsurePairedAsync(BluetoothLEDevice device)
    {
        var pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired) return "already-paired";
        if (!pairing.CanPair) return "cannot-pair";

        var custom = pairing.Custom;
        // Accept whatever ceremony the printer asks for (Just Works / confirm / pin).
        void OnRequested(DeviceInformationCustomPairing _,
                         DevicePairingRequestedEventArgs e)
        {
            if (e.PairingKind == DevicePairingKinds.ProvidePin)
                e.Accept("0000");           // common default for cheap BLE printers
            else
                e.Accept();
        }
        custom.PairingRequested += OnRequested;
        try
        {
            var result = await custom.PairAsync(
                DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ProvidePin |
                DevicePairingKinds.ConfirmPinMatch,
                DevicePairingProtectionLevel.None);
            return result.Status.ToString();
        }
        finally
        {
            custom.PairingRequested -= OnRequested;
        }
    }

    private static async Task<GattCharacteristic> GetCharacteristicAsync(GattDeviceService service, Guid uuid)
    {
        var res = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (res.Status != GattCommunicationStatus.Success || res.Characteristics.Count == 0)
            throw new InvalidOperationException($"Characteristic {uuid} not found ({res.Status}).");
        return res.Characteristics[0];
    }

    private static async Task EnableNotificationsAsync(GattCharacteristic ch)
    {
        var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Could not enable notifications on {ch.Uuid} ({status}).");
    }

    private void OnRxChanged(GattCharacteristic _, GattValueChangedEventArgs args)
    {
        var data = ReadBytes(args.CharacteristicValue);
        if (data.Length >= 2)
            StatusReported?.Invoke(new P15Status(data[0], data[1]));
        else if (data.Length == 1)
            StatusReported?.Invoke(new P15Status(data[0], 0));
    }

    private void OnCxChanged(GattCharacteristic _, GattValueChangedEventArgs args)
    {
        var data = ReadBytes(args.CharacteristicValue);
        if (data.Length >= 2 && data[0] == 0x01)
        {
            // Credit grant: release that many slots (clamped to the semaphore max).
            int n = data[1];
            for (int i = 0; i < n; i++)
            {
                try { _credits.Release(); } catch (SemaphoreFullException) { break; }
            }
        }
        // [0x02, lo, hi] MTU advert is informational; we always chunk to PacketSize.
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    // ----------------------------------------------------------------------
    //  High-level print API
    // ----------------------------------------------------------------------

    /// <summary>
    /// Prints a 1-bit raster page. <paramref name="bitmap"/> must be row-major,
    /// MSB-first packed (bit7 = leftmost pixel, 1 = black), with
    /// <paramref name="widthBytes"/> bytes per row and <paramref name="height"/> rows.
    /// </summary>
    /// <param name="density">Burn darkness 0..15 (typical 8).</param>
    public async Task PrintRasterAsync(byte[] bitmap, int widthBytes, int height, byte density = 8)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (widthBytes <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthBytes));
        if (bitmap.Length < widthBytes * height)
            throw new ArgumentException("Bitmap buffer is smaller than widthBytes * height.");

        // --- Init ---
        await SendAsync(new byte[15]);                               // wakeup (15 zero bytes)
        await SendAsync(new byte[] { 0x10, 0xFF, 0xF1, 0x02 });      // enable printing
        await SendAsync(new byte[] { 0x1F, 0x70, 0x02, density });   // set density

        // --- Raster (GS v 0): 1D 76 30 m xL xH yL yH <data> ---
        var header = new byte[]
        {
            0x1D, 0x76, 0x30, 0x00,
            (byte)(widthBytes & 0xFF), (byte)((widthBytes >> 8) & 0xFF),
            (byte)(height & 0xFF),     (byte)((height >> 8) & 0xFF),
        };
        var payload = new byte[header.Length + widthBytes * height];
        System.Buffer.BlockCopy(header, 0, payload, 0, header.Length);
        System.Buffer.BlockCopy(bitmap, 0, payload, header.Length, widthBytes * height);
        await SendAsync(payload);

        // --- Finish ---
        await SendAsync(new byte[] { 0x1B, 0x4A, 0x64 });            // feed 100 dots to tear-off
        await SendAsync(new byte[] { 0x10, 0xFF, 0xF1, 0x45 });      // stop print job
    }

    /// <summary>Manually advance the label by <paramref name="dots"/> (0..255).</summary>
    public Task FeedAsync(byte dots = 100) =>
        SendAsync(new byte[] { 0x1B, 0x4A, dots });

    /// <summary>Feed to the next gap/black-mark (form feed).</summary>
    public Task FormFeedAsync() =>
        SendAsync(new byte[] { 0x1D, 0x0C });

    /// <summary>
    /// Writes a raw byte stream to TX, split into 95-byte packets and paced
    /// by the printer's credit grants.
    /// </summary>
    public async Task SendAsync(byte[] data)
    {
        if (_tx is null) throw new InvalidOperationException("Not connected.");

        for (int offset = 0; offset < data.Length; offset += PacketSize)
        {
            // Wait for a credit; fall back after a short timeout so an
            // under-talkative firmware can't deadlock the stream.
            if (!await _credits.WaitAsync(2000))
            {
                // proceed best-effort
            }

            int len = Math.Min(PacketSize, data.Length - offset);
            var chunk = new byte[len];
            System.Buffer.BlockCopy(data, offset, chunk, 0, len);

            var status = await _tx.WriteValueAsync(
                chunk.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            if (status != GattCommunicationStatus.Success)
                throw new IOException($"BLE write failed ({status}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_rx is not null) _rx.ValueChanged -= OnRxChanged;
            if (_cx is not null) _cx.ValueChanged -= OnCxChanged;
        }
        catch { /* ignore */ }

        _session?.Dispose();
        _device?.Dispose();
        _credits.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>A status byte pair reported by the printer on the RX characteristic.</summary>
public readonly record struct P15Status(byte First, byte Second)
{
    public bool IsError => First == 0xFF;
    public bool IsSuccess => First is 0xAA or 0x4F or 0x4B;

    public string Describe() => First switch
    {
        0xFF => Second switch
        {
            0x01 => "Paper out",
            0x02 => "Cover open",
            0x03 => "Overheating",
            0x04 => "Low battery",
            0x05 => "Cover closed",
            _    => $"Error 0x{Second:X2}",
        },
        0xAA or 0x4F or 0x4B => "Print OK",
        _ => $"Status 0x{First:X2} 0x{Second:X2}",
    };
}
