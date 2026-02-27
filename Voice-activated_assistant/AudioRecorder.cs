using NAudio.Wave;

namespace Voice_activated_assistant
{
    /// <summary>
    /// 使用 NAudio 錄音
    /// </summary>
    public class AudioRecorder : IDisposable
    {
        private WaveInEvent? waveSource = null;
        private WaveFileWriter? waveFile = null;
        private readonly MemoryStream memoryStream = new MemoryStream();
        private bool isRecording = false;
        private bool isSpeaking = false;
        private float threshold = 0.005f; // 起始門檻，會隨環境自動調整
        private float noiseFloor = 0.002f; // 環境底噪
        private DateTime lastVoiceTime = DateTime.MinValue;
        private readonly int silenceDurationMs = 900; // 縮短為 0.9 秒，提升反應速度

        // 預錄緩衝區：保存觸發前約 600ms 的音訊 (確保起手字完整)
        private readonly List<byte[]> preRollBuffer = new List<byte[]>();
        private readonly int maxPreRollBlocks = 30; 

        public AudioRecorder()
        {
            // 初始化錄音設備並保持長駐，避免重覆建立
            waveSource = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 1)
            };
            waveSource.DataAvailable += WaveSource_DataAvailable;
            waveSource.StartRecording();
        }

        public void StartRecording()
        {
            // 重置記憶體流而不重新分配空間
            lock (memoryStream)
            {
                memoryStream.SetLength(0);
                memoryStream.Position = 0;
            }
            isRecording = true;
            isSpeaking = false;
            lastVoiceTime = DateTime.MinValue;
            preRollBuffer.Clear();
        }

        private int voiceConfirmCount = 0; // 用於確認是否為持續的人聲而非突發雜訊

        private void WaveSource_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!isRecording) return;

            float maxAmplitude = 0;
            float sumAmplitude = 0;
            for (int index = 0; index < e.BytesRecorded; index += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, index);
                float absSample = Math.Abs(sample / 32768f);
                if (absSample > maxAmplitude) maxAmplitude = absSample;
                sumAmplitude += absSample;
            }
            float avgAmplitude = sumAmplitude / (e.BytesRecorded / 2);

            // 動態調整底噪：在沒人說話時，學習環境音
            if (!isSpeaking)
            {
                noiseFloor = (noiseFloor * 0.98f) + (avgAmplitude * 0.02f); // 更緩慢、穩定的學習
                threshold = Math.Max(0.012f, noiseFloor * 2.0f + 0.005f); // 拉開門檻，底噪的兩倍再加上一個基本偏移，過濾雜訊
            }

            // 必須同時滿足：1. 峰值超過門檻 2. 平均能量也要有一定的水平
            if (maxAmplitude > threshold && avgAmplitude > threshold * 0.3f)
            {
                voiceConfirmCount++;
                
                // 需要連續 2 個 block (約 80ms) 都偵測到聲音，才判定為「正在說話」
                if (voiceConfirmCount >= 2)
                {
                    lastVoiceTime = DateTime.Now;
                    if (!isSpeaking)
                    {
                        isSpeaking = true;
                        Console.WriteLine("\n🎤 偵測到聲音 (穩定)...");
                        
                        lock (memoryStream)
                        {
                            if (waveFile == null)
                            {
                                waveFile = new WaveFileWriter(new IgnoreDisposeStream(memoryStream), waveSource!.WaveFormat);
                                foreach (var block in preRollBuffer)
                                {
                                    waveFile.Write(block, 0, block.Length);
                                }
                                preRollBuffer.Clear();
                            }
                        }
                    }
                }
            }
            else
            {
                voiceConfirmCount = 0; // 一旦中斷就重算，過濾突發性的快響 (如滑鼠點擊)
            }

            // 如果已經判定為說話中，則無論音量大小都持續錄音，直到停頓偵測發動
            if (isSpeaking)
            {
                lock (memoryStream) { waveFile?.Write(e.Buffer, 0, e.BytesRecorded); }
            }
            else
            {
                preRollBuffer.Add(e.Buffer.ToArray());
                if (preRollBuffer.Count > maxPreRollBlocks) preRollBuffer.RemoveAt(0);
            }
        }

        public void StopRecording()
        {
            isRecording = false;
            isSpeaking = false;
            lock (memoryStream)
            {
                waveFile?.Dispose();
                waveFile = null;
            }
        }

        public Stream? GetAudioStream()
        {
            lock (memoryStream)
            {
                if (memoryStream.Length < 1000) return null;
                // Console.WriteLine($"📦 準備辨識音訊流 (大小: {memoryStream.Length / 1024.0:F2} KB)"); // Removed as per instruction
                return new MemoryStream(memoryStream.ToArray());
            }
        }

        public bool IsRecording() => isRecording;
        // public bool IsSpeaking() => isSpeaking; // Removed as per instruction
        public bool ShouldStopDueToSilence() => isRecording && isSpeaking && (DateTime.Now - lastVoiceTime).TotalMilliseconds > silenceDurationMs;

        public void Dispose()
        {
            waveSource?.StopRecording();
            waveSource?.Dispose();
            waveFile?.Dispose();
            memoryStream.Dispose();
        }

        private class IgnoreDisposeStream : Stream
        {
            private readonly Stream _inner;
            public IgnoreDisposeStream(Stream inner) => _inner = inner;
            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            protected override void Dispose(bool disposing) { }
        }
    }
}
