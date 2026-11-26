
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.MiniAudio;
using Spectre.Console;

namespace OpenTone;
using StatsCallbackFunc = Func<(long, long, int, int), int>;   

public class AudioCapture : IDisposable
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
}
