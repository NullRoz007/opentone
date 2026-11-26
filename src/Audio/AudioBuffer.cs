namespace OpenTone;

public class AudioBuffer
{
    private const int Channels = 2;
    private const int AudioBufferCapacity = 128;
    private const int AudioBufferMask = AudioBufferCapacity - 1;

    private readonly short[] audioBuffer = new short[AudioBufferCapacity];

    private long writePosition;
    private long readPosition;

    public unsafe int Write(short* samples, int sampleCount)
    {
        long write = Volatile.Read(ref writePosition);
        long read = Volatile.Read(ref readPosition);

        int written = 0;

        while (written < sampleCount && write - read < AudioBufferCapacity)
        {
            audioBuffer[(int)(write & AudioBufferMask)] = samples[written];
            write++;
            written++;
        }

        Volatile.Write(ref writePosition, write);

        return written;
    }

    public unsafe int Read(short* destination, int sampleCount)
    {
        long read = Volatile.Read(ref readPosition);
        long write = Volatile.Read(ref writePosition);

        int readCount = 0;

        while (readCount < sampleCount && read < write)
        {
            destination[readCount] = audioBuffer[(int)(read & AudioBufferMask)];
            read++;
            readCount++;
        }

        for (int i = readCount; i < sampleCount; i++) // fill remaining samples with silence
            destination[i] = 0;

        Volatile.Write(ref readPosition, read);

        return readCount;
    }

    public int AvailableSamples => (int)(Volatile.Read(ref writePosition) - Volatile.Read(ref readPosition));

    public short PeakAmplitude()
    {
        long read = Volatile.Read(ref readPosition);
        long write = Volatile.Read(ref writePosition);

        short peak = 0;

        for (long i = read; i < write; i++)
        {
            short sample = audioBuffer[(int)(i & AudioBufferMask)];

            if (Math.Abs((int)sample) > peak)
                peak = (short)Math.Abs((int)sample);
        }

        return peak;
    }
}