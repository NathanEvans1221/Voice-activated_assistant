/**
 * 語音助理啟動說明：
 * 1. 確保已安裝 .NET 10 Runtime / SDK。
 * 2. 在終端機執行 `dotnet run` 即可啟動。
 * 3. 程式啟動後會自動監測麥克風，每 10 秒進行一次語音轉文字。
 * 4. 按下 ESC 鍵可停止程式。
 */

// See https://aka.ms/new-console-template for more information
using Voice_activated_assistant;
using Whisper.net;
using Whisper.net.Ggml;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Collections.Generic;

// 指定輸出為 UTF8
Console.OutputEncoding = System.Text.Encoding.UTF8;

string currentDirectory = Environment.CurrentDirectory;
Console.WriteLine($"目前的工作目錄: {currentDirectory}");

Console.WriteLine("\n請選擇使用的模型版本：");
Console.WriteLine("1. 官方最小模型 (Tiny, 約 31MB) - [自動下載]");
Console.WriteLine("2. 繁體中文微調模型 (Tiny-zh-TW, 約 74MB) - [需手動下載]");
Console.WriteLine("3. 官方基礎模型 (Base, 約 73MB) - [自動下載, CP值最高]");
Console.WriteLine("4. 官方小型模型 (Small, 約 153MB) - [自動下載, 準確與效能兼備]");
Console.WriteLine("5. 官方中型模型 (Medium, 約 469MB) - [自動下載, 適合中階配備, 幻覺極低]");
Console.Write("請輸入選擇 (1, 2, 3, 4 或 5，預設為 1): ");

string choice = Console.ReadLine() ?? "1";
string modelName;
GgmlType? downloadType = null;

if (choice == "2")
{
    modelName = "ggml-tiny-zh_tw.bin";
    if (!File.Exists(modelName))
    {
        Console.WriteLine($"\n❌ 找不到繁體中文模型檔案: {modelName}");
        Console.WriteLine("請至以下網址下載並放入程式目錄後重新執行：");
        Console.WriteLine("https://huggingface.co/xmzhu/whisper-tiny-zh-TW/resolve/main/ggml-tiny-zh_tw.bin");
        Console.WriteLine("\n按任意鍵結束...");
        Console.ReadKey();
        return;
    }
}
else if (choice == "3")
{
    modelName = "ggml-base-q5_1.bin";
    downloadType = GgmlType.Base;
}
else if (choice == "4")
{
    modelName = "ggml-small-q5_1.bin";
    downloadType = GgmlType.Small;
}
else if (choice == "5")
{
    modelName = "ggml-medium-q5_1.bin";
    downloadType = GgmlType.Medium;
}
else
{
    modelName = "ggml-tiny-q5_1.bin";
    downloadType = GgmlType.Tiny;
}

if (downloadType != null)
{
    if (File.Exists(modelName))
    {
        Console.WriteLine($"✅ {modelName} 檔案已經存在，不須下載模型");
    }
    else
    {
        Console.WriteLine($"\n🈚 {modelName} 檔案不存在，準備從官方下載模式 ({downloadType})");

        using var httpClient = new HttpClient();
        using var modelStream = await new WhisperGgmlDownloader(httpClient).GetGgmlModelAsync(downloadType.Value, QuantizationType.Q5_1);
        using var fileWriter = File.OpenWrite(modelName);
        await modelStream.CopyToAsync(fileWriter);
        Console.WriteLine($"✅ {modelName} 下載完成！");
    }
}

Console.WriteLine($"\n🚀 正在啟動語音助理，使用模型: {modelName}");
using var whisperFactory = WhisperFactory.FromPath(modelName);
using var processor = whisperFactory.CreateBuilder()
    .WithLanguage("zh") 
    .WithPrompt("你好。嗨。請問有什麼事嗎？") 
    .WithTemperature(0.0f) 
    .WithNoSpeechThreshold(0.6f) 
    .WithLogProbThreshold(-1.0f) 
    .WithThreads(Environment.ProcessorCount)
    .Build();

// 降低處理續優先權，讓它在背景執行時不干擾主程式
using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
currentProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;

using var recorder = new AudioRecorder();
bool isRunning = true;

// 初始化 TTS
using var synth = new SpeechSynthesizer();
synth.SetOutputToDefaultAudioDevice();

string readyMsg = "程式準備完畢，請說話！";
Console.WriteLine($"\n✅ {readyMsg}\n");
synth.Speak(readyMsg); // 使用同步播放，確保說完才進入監聽迴圈

// 讀取關鍵字回應表
Dictionary<string, string> keywordResponses = new();
string configPath = "keyword_responses.json";
if (File.Exists(configPath))
{
    try 
    {
        string json = File.ReadAllText(configPath);
        keywordResponses = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        Console.WriteLine($"📊 已載入 {keywordResponses.Count} 筆關鍵字回應設定。");
    }
    catch (Exception ex) { Console.WriteLine($"⚠️ 載入設定表失敗: {ex.Message}"); }
}

// 用於按順序排隊轉譯任務，但不阻塞錄音
var transcriptionQueue = Task.CompletedTask;

while (isRunning)
{
    // 檢查 TTS 是否正在說話，若是則等待（防止錄到自己的聲音）
    while (synth.State == SynthesizerState.Speaking)
    {
        await Task.Delay(200);
    }

    recorder.StartRecording();
    
    int maxWaitMs = 15000; 
    int waitedMs = 0;
    while (waitedMs < maxWaitMs)
    {
        // 核心保護：如果正在監聽時，背景任務啟動了 TTS 回話，則立即斷開錄音避免回授
        if (synth.State == SynthesizerState.Speaking)
        {
            Console.WriteLine("\n🔇 偵測到助理回話，暫時停止監聽並重新計時...");
            break;
        }

        int elapsedSeconds = waitedMs / 1000;
        Console.Write($"\r🎙️  監聽中 ({elapsedSeconds}s)...".PadRight(20));

        if (recorder.ShouldStopDueToSilence()) 
        {
            Console.WriteLine("\n🛑 偵測到停頓，派發辨識中...");
            break;
        }
        await Task.Delay(100);
        waitedMs += 100;
        
        if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) 
        {
            isRunning = false;
            break;
        }
    }
    
    recorder.StopRecording();
    if (!isRunning) break;

    // 獲取音訊流副本 (AudioRecorder 已實作製作獨立副本)
    var audioStream = recorder.GetAudioStream();
    if (audioStream != null && audioStream.Length > 0)
    {
        // 核心優化：將轉譯工作交給背景隊列，主執行緒立即回到迴圈開始下一段錄製
        transcriptionQueue = transcriptionQueue.ContinueWith(async _ => 
        {
            try 
            {
                using (audioStream)
                {
                    await foreach (var result in processor.ProcessAsync(audioStream))
                    {
                        if (!string.IsNullOrWhiteSpace(result.Text))
                        {
                            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] {result.Text}");
                            
                            // 關鍵字檢索與 TTS 回應
                            foreach (var kvp in keywordResponses)
                            {
                                if (result.Text.Contains(kvp.Key))
                                {
                                    string finalResponse = kvp.Value.Replace("{TIME}", DateTime.Now.ToString("HH:mm"));
                                    Console.WriteLine($"🤖 [關鍵字觸發] 「{kvp.Key}」 -> {finalResponse}");
                                    synth.SpeakAsync(finalResponse);
                                    break; // 每次對話只觸發一個關鍵字
                                }
                            }
                        }
                    }
                }
                GC.Collect(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ 背景轉譯發生錯誤: {ex.Message}");
            }
        }).Unwrap();
    }
}

// 結束前確保最後一段轉譯有跑完
await transcriptionQueue;

Console.WriteLine("✅ 程式已結束!");
Console.ReadLine();
