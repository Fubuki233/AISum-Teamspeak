namespace TsAi.Models;

public record VoicePacket(ushort ClientId, uint Frequency, int SampleCount, byte[] AudioData);
