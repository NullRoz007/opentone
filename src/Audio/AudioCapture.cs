
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Hexa.NET.MiniAudio;
using Spectre.Console;

namespace OpenTone;
using StatsCallbackFunc = Func<(long, long, int, int), int>;   

public partial class AudioCapture : IDisposable
{
    private unsafe MaContext* context;
    private unsafe MaDevice* captureDevice;
    private unsafe MaDevice* playbackDevice;

    private MaResult maResult;

    private MaDeviceInfo captureDeviceInfo;
    private MaDeviceInfo playbackDeviceInfo;

    private bool captureInitialized;
    private bool playbackInitialized;
    private bool disposed;

    private readonly AudioBuffer audioBuffer = new();
    
    private readonly AudioCallbackState callbackState;
    private GCHandle audioBufferHandle;

    // stats
    public long CaptureCallbackCount =>
        Interlocked.Read(ref callbackState.CaptureCallbackCount);
    public long PlaybackCallbackCount =>
        Interlocked.Read(ref callbackState.PlaybackCallbackCount);
    public int BufferedSamples => audioBuffer.AvailableSamples;
    public int PeakAmplitude => audioBuffer.PeakAmplitude();

    
    private sealed class AudioCallbackState
    {
        public readonly AudioBuffer Buffer;

        public long CaptureCallbackCount;
        public long PlaybackCallbackCount;

        public AudioCallbackState(AudioBuffer buffer)
        {
            Buffer = buffer;
        }
    }

    public AudioCapture()
    {
        callbackState = new AudioCallbackState(audioBuffer);
        
        unsafe {
            context = (MaContext*)NativeMemory.Alloc((nuint)sizeof(MaContext));

            if (context == null)
            {
                Dispose();
                throw new OutOfMemoryException();
            }

            var config = MiniAudio.ContextConfigInit();
            maResult = MiniAudio.ContextInit(null, 0, &config, context);

            if (maResult != MaResult.Success)
            {
                Dispose();
                throw new InvalidOperationException(
                    $"Failed to initialise MiniAudio context: {maResult}");
            }
        }
    }

    public AudioCapture(object inputDeviceId, object outputDeviceId)
    {
        callbackState = new AudioCallbackState(audioBuffer);

        unsafe
        {
            context = (MaContext*)NativeMemory.Alloc((nuint)sizeof(MaContext));
            captureDevice = (MaDevice*)NativeMemory.Alloc((nuint)sizeof(MaDevice));
            playbackDevice = (MaDevice*)NativeMemory.Alloc((nuint)sizeof(MaDevice));

            if (context == null || captureDevice == null || playbackDevice == null)
            {
                Dispose();
                throw new OutOfMemoryException();
            }

            var config = MiniAudio.ContextConfigInit();
            maResult = MiniAudio.ContextInit(null, 0, &config, context);

            if (maResult != MaResult.Success)
            {
                Dispose();
                throw new InvalidOperationException(
                    $"Failed to initialise MiniAudio context: {maResult}");
            }

            try
            {
                InitDevices(inputDeviceId, outputDeviceId);
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    public unsafe object ResolveDeviceId(MaDeviceInfo device)
    {
        switch (context->Backend)
        {
            case MaBackend.Wasapi:
            {
                return Marshal.PtrToStringUni((nint)(&device.Id.Wasapi_0))!;
            }
            default:
            throw new InvalidOperationException(
                $"Unsupported active backend: {context->Backend}");
        }
    }

    public unsafe string BackendName() => context->Backend.ToString();

    /// <summary>
    /// The capture device ID of the USB audio interface of <paramref name="amp"/>, matched by VID, PID and
    /// serial number (not by name). If the amp reports no serial, the VID and PID must match exactly one
    /// capture device. IDs come from <see cref="ResolveDeviceId"/>, so the result can be passed straight
    /// to the constructor on any backend that <see cref="ResolveUsbIdentity"/> supports.
    /// </summary>
    public object FindCaptureDevice(FenderDevices.HardwareInfo amp)
    {
        var (_, captureDevices) = EnumerateDevices();

        var candidates = captureDevices
            .Select(ResolveDeviceId)
            .Select(id => (Id: id, Usb: ResolveUsbIdentity(id)))
            .Where(c => c.Usb is { } u && u.Vid == amp.Vid && u.Pid == amp.Pid)
            .ToList();

        if (amp.Serial != null)
            candidates = candidates
                .Where(c => string.Equals(c.Usb!.Value.Serial, amp.Serial, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return candidates.Count switch
        {
            1 => candidates[0].Id,
            0 => throw new InvalidOperationException(
                $"No capture device belongs to the amp ({amp.Vid:X4}:{amp.Pid:X4}, serial {amp.Serial ?? "none"}). " +
                "Check that the system lists its \"Internal AUX Jack\" recording device."),
            _ => throw new InvalidOperationException(
                $"{candidates.Count} capture devices match the amp and it has no serial number to tell them apart. Pass the capture ID instead."),
        };
    }

    /// <summary>
    /// The USB device behind a device ID from <see cref="ResolveDeviceId"/>, or null if it isn't a USB
    /// device. This is the one backend-specific step; add a case per backend alongside ResolveDeviceId.
    /// </summary>
    public unsafe (ushort Vid, ushort Pid, string? Serial)? ResolveUsbIdentity(object deviceId)
    {
        switch (context->Backend)
        {
            case MaBackend.Wasapi when OperatingSystem.IsWindows():
                return FindWasapiUsbDevice((string)deviceId);
            default:
                throw new PlatformNotSupportedException(
                    $"Finding the amp's capture device is not supported on backend {context->Backend}. Pass the capture ID instead.");
        }
    }

    private void InitDevices(object inputDeviceId, object outputDeviceId)
    {
        var (playbackDevices, captureDevices) = EnumerateDevices();
        var inputDeviceInfo = captureDevices.Find(d => Equals(ResolveDeviceId(d), inputDeviceId));
        var outputDeviceInfo = playbackDevices.Find(d => Equals(ResolveDeviceId(d), outputDeviceId));

        if (inputDeviceInfo.Equals(default(MaDeviceInfo)))
            throw new InvalidOperationException(
                $"Capture device not found: {inputDeviceId}");

        if (outputDeviceInfo.Equals(default(MaDeviceInfo)))
            throw new InvalidOperationException(
                $"Playback device not found: {outputDeviceId}");

        captureDeviceInfo = inputDeviceInfo;
        playbackDeviceInfo = outputDeviceInfo;

        // Keep the callback state alive for both native devices.
        audioBufferHandle = GCHandle.Alloc(callbackState);

        InitCaptureDevice();
        InitPlaybackDevice();
    }

    private static readonly unsafe delegate* unmanaged[Cdecl]<MaDevice*, void*, void*, uint, void>
        CaptureCallback = &_CaptureCallback;

    private static readonly unsafe delegate* unmanaged[Cdecl]<MaDevice*, void*, void*, uint, void>
        PlaybackCallback = &_PlaybackCallback;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void _CaptureCallback(
        MaDevice* device,
        void* output,
        void* input,
        uint frameCount)
    {
        if (input == null || device->PUserData == null)
            return;

        GCHandle handle = GCHandle.FromIntPtr((nint)device->PUserData);
        AudioCallbackState state = (AudioCallbackState)handle.Target!;

        Interlocked.Increment(ref state.CaptureCallbackCount);

        short* samples = (short*)input;
        uint sampleCount = frameCount * device->Capture.Channels;

        state.Buffer.Write(samples, (int)sampleCount);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void _PlaybackCallback(
        MaDevice* device,
        void* output,
        void* input,
        uint frameCount)
    {
        if (output == null || device->PUserData == null)
            return;

        GCHandle handle = GCHandle.FromIntPtr((nint)device->PUserData);
        AudioCallbackState state = (AudioCallbackState)handle.Target!;

        Interlocked.Increment(ref state.PlaybackCallbackCount);

        short* samples = (short*)output;
        uint sampleCount = frameCount * device->Playback.Channels;

        state.Buffer.Read(samples, (int)sampleCount);
    }

    private unsafe void InitCaptureDevice()
    {
        var deviceConfig = MiniAudio.DeviceConfigInit(MaDeviceType.Capture);

        fixed (MaDeviceInfo* deviceInfo = &captureDeviceInfo)
        {
            deviceConfig.Capture.PDeviceID = &deviceInfo->Id;
            deviceConfig.Capture.Format = MaFormat.S16;
            deviceConfig.Capture.Channels = 2;
            deviceConfig.SampleRate = 48000;
            deviceConfig.PUserData = (void*)GCHandle.ToIntPtr(audioBufferHandle);
            deviceConfig.DataCallback = CaptureCallback;

            maResult = MiniAudio.DeviceInit(context, in deviceConfig, captureDevice);
        }

        if (maResult != MaResult.Success)
            throw new InvalidOperationException(
                $"Capture device initialization failed: {maResult}");

        captureInitialized = true;
    }

    private unsafe void InitPlaybackDevice()
    {
        var deviceConfig = MiniAudio.DeviceConfigInit(MaDeviceType.Playback);

        fixed (MaDeviceInfo* deviceInfo = &playbackDeviceInfo)
        {
            deviceConfig.Playback.PDeviceID = &deviceInfo->Id;
            deviceConfig.Playback.Format = MaFormat.S16;
            deviceConfig.Playback.Channels = 2;
            deviceConfig.SampleRate = 48000;
            deviceConfig.PUserData = (void*)GCHandle.ToIntPtr(audioBufferHandle);
            deviceConfig.DataCallback = PlaybackCallback;

            maResult = MiniAudio.DeviceInit(context, in deviceConfig, playbackDevice);
        }

        if (maResult != MaResult.Success)
            throw new InvalidOperationException(
                $"Playback device initialization failed: {maResult}");

        playbackInitialized = true;
    }

    public unsafe (
        List<MaDeviceInfo> playbackDevices,
        List<MaDeviceInfo> captureDevices
    ) EnumerateDevices()
    {
        List<MaDeviceInfo> playbackDeviceList = new();
        List<MaDeviceInfo> captureDeviceList = new();

        MaDeviceInfo* playbackDevices = null;
        MaDeviceInfo* captureDevices = null;

        uint playbackDeviceCount = 0;
        uint captureDeviceCount = 0;

        maResult = MiniAudio.ContextGetDevices(
            context,
            ref playbackDevices,
            ref playbackDeviceCount,
            ref captureDevices,
            ref captureDeviceCount);

        if (maResult != MaResult.Success)
            throw new InvalidOperationException(
                $"Device enumeration failed: {maResult}");

        for (uint i = 0; i < captureDeviceCount; i++)
            captureDeviceList.Add(captureDevices[i]);

        for (uint i = 0; i < playbackDeviceCount; i++)
            playbackDeviceList.Add(playbackDevices[i]);

        return (playbackDeviceList, captureDeviceList);
    }
              
    public async Task StartCapture(StatsCallbackFunc statsCallback, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        unsafe
        {
            maResult = MiniAudio.DeviceStart(playbackDevice);

            if (maResult != MaResult.Success)
                throw new InvalidOperationException(
                    $"Failed to start playback: {maResult}");

            maResult = MiniAudio.DeviceStart(captureDevice);

            if (maResult != MaResult.Success)
            {
                MiniAudio.DeviceStop(playbackDevice);

                throw new InvalidOperationException(
                    $"Failed to start capture: {maResult}");
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                statsCallback((
                    CaptureCallbackCount, 
                    PlaybackCallbackCount, 
                    BufferedSamples, 
                    PeakAmplitude
                ));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Capture cancellation requested.
        }
        finally
        {
            StopCapture();
        }
    }

    private unsafe void StopCapture()
    {
        if (captureInitialized && captureDevice != null)
            MiniAudio.DeviceStop(captureDevice);

        if (playbackInitialized && playbackDevice != null)
            MiniAudio.DeviceStop(playbackDevice);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        unsafe
        {
            if (captureDevice != null)
            {
                if (captureInitialized)
                    MiniAudio.DeviceUninit(captureDevice);

                NativeMemory.Free(captureDevice);
                captureDevice = null;
                captureInitialized = false;
            }

            if (playbackDevice != null)
            {
                if (playbackInitialized)
                    MiniAudio.DeviceUninit(playbackDevice);

                NativeMemory.Free(playbackDevice);
                playbackDevice = null;
                playbackInitialized = false;
            }

            if (audioBufferHandle.IsAllocated)
                audioBufferHandle.Free();

            if (context != null)
            {
                MiniAudio.ContextUninit(context);
                NativeMemory.Free(context);
                context = null;
            }
        }

        GC.SuppressFinalize(this);
    }

    // ---- Windows PnP tree (cfgmgr32), used by ResolveUsbIdentity for WASAPI ----

    private const int CR_SUCCESS = 0;
    private const int MaxPnpDepth = 8;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    private static partial int CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static unsafe partial int CM_Get_Device_ID(uint devInst, char* buffer, uint length, uint flags);

    // USB\VID_xxxx&PID_xxxx\<instance>, without &MI_nn (that would be one interface of a composite device).
    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex UsbDeviceId();

    /// <summary>
    /// The USB device behind a WASAPI endpoint, found by walking up the Windows PnP tree from the
    /// endpoint's devnode. Null if the endpoint isn't present or isn't a USB device. For the LT25:
    /// <code>
    /// SWD\MMDEVAPI\{0.0.1.00000000}.{guid}           audio endpoint (the WASAPI ID miniaudio returns)
    ///   USB\VID_1ED8&amp;PID_0037&amp;MI_01\8&amp;...&amp;0001     audio interface of the composite device
    ///     USB\VID_1ED8&amp;PID_0037\00000000001A         the USB device; the last part is its serial number
    /// </code>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (ushort Vid, ushort Pid, string? Serial)? FindWasapiUsbDevice(string wasapiId)
    {
        if (CM_Locate_DevNode(out uint node, @"SWD\MMDEVAPI\" + wasapiId, 0) != CR_SUCCESS)
            return null;

        for (int depth = 0; depth < MaxPnpDepth; depth++)
        {
            if (CM_Get_Parent(out node, node, 0) != CR_SUCCESS) return null;

            string? id = PnpDeviceId(node);
            if (id == null) return null;

            var m = UsbDeviceId().Match(id);
            if (!m.Success) continue;           // the endpoint itself, an MI_nn interface, or a non-USB node

            string instance = m.Groups[3].Value;
            return (
                ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber),
                ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber),
                // Windows uses the device's serial as the instance ID when it has a usable one. Otherwise
                // it generates one from the port, and that always contains '&'.
                instance.Contains('&') ? null : instance);
        }
        return null;
    }

    private static unsafe string? PnpDeviceId(uint node)
    {
        if (CM_Get_Device_ID_Size(out uint length, node, 0) != CR_SUCCESS) return null;
        var buffer = new char[length + 1];      // the size excludes the NUL terminator
        fixed (char* p = buffer)
        {
            if (CM_Get_Device_ID(node, p, (uint)buffer.Length, 0) != CR_SUCCESS) return null;
        }
        return new string(buffer, 0, (int)length);
    }
}
