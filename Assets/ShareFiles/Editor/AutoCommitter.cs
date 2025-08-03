using UnityEditor;
using UnityEngine;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Net.Http;
using System.Linq;
using System.Text;
using System;
using Newtonsoft.Json;

public class AutoCommitter : EditorWindow
{
    private static string apiKey = "";
    private static string gitRepoPath = "";
    private static string commitMessage = "以下はGitの差分や変更ファイルリストです。この情報に基づいて、変更内容を要約したコミットメッセージを生成してください";
    private static int modificationCount = 0; // セッションごとにカウント
    private static int modificationCountThreshold = 5;
    private const int MaxGeminiInputLength = 4000; // しきい値（適宜調整OK）
    private const string SettingsPath = "Assets/Editor/AutoCommitterSettings.json";
    private static bool isCommitInProgress = false;

    [MenuItem("Tools/Auto Git Committer")]
    public static void ShowWindow()
    {
        EditorWindow.GetWindow(typeof(AutoCommitter), false, "Auto Git Committer");
    }

    private void OnEnable()
    {
        LoadSettings();
        if (string.IsNullOrEmpty(gitRepoPath))
        {
            gitRepoPath = FindGitRepositoryPath();
            if (!string.IsNullOrEmpty(gitRepoPath))
                UnityEngine.Debug.Log("Gitリポジトリ自動検出: " + gitRepoPath);
        }
        modificationCount = 0; // セッション開始時にリセット
    }

    private void OnGUI()
    {
        GUILayout.Label("Git Committer Settings", EditorStyles.boldLabel);
        apiKey = EditorGUILayout.TextField("Gemini API Key", apiKey);
        gitRepoPath = EditorGUILayout.TextField("Git Repository Path", gitRepoPath);
        commitMessage = EditorGUILayout.TextField("Base Commit Message", commitMessage);
        modificationCountThreshold = EditorGUILayout.IntField("Commit Modification Count Threshold", modificationCountThreshold);
        EditorGUILayout.LabelField("Current Modification Count (Session only)", modificationCount.ToString());

        if (GUILayout.Button("Manual Commit"))
        {
            CommitChanges(GetModifiedFiles());
        }
        if (GUILayout.Button("Save Settings"))
        {
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        var settings = new Settings
        {
            apiKey = apiKey,
            gitRepoPath = gitRepoPath,
            commitMessage = commitMessage,
            modificationCountThreshold = modificationCountThreshold
        };
        var json = JsonUtility.ToJson(settings);
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, json);
        UnityEngine.Debug.Log("Settings saved.");
    }

    private void LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        var json = File.ReadAllText(SettingsPath);
        var settings = JsonUtility.FromJson<Settings>(json);
        apiKey = settings.apiKey;
        gitRepoPath = settings.gitRepoPath;
        commitMessage = settings.commitMessage;
        modificationCountThreshold = settings.modificationCountThreshold == 0 ? 5 : settings.modificationCountThreshold;
    }

    public static string FindGitRepositoryPath()
    {
        string dir = Application.dataPath;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }
        return "";
    }

    public static string[] GetModifiedFiles()
    {
        string repoPath = string.IsNullOrEmpty(gitRepoPath) ? FindGitRepositoryPath() : gitRepoPath;
        if (string.IsNullOrEmpty(repoPath))
        {
            UnityEngine.Debug.LogWarning("gitリポジトリが見つからなかったので変更ファイル取得できなかったよ～");
            return new string[0];
        }
        ProcessStartInfo startInfo = new ProcessStartInfo()
        {
            FileName = "git",
            Arguments = "diff --name-only",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process process = new Process() { StartInfo = startInfo };
        process.Start();
        string gitDiff = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return gitDiff.Split('\n').Select(f => f.Trim()).Where(f => !string.IsNullOrEmpty(f)).ToArray();
    }

    private static string GetGitDiff()
    {
        string repoPath = string.IsNullOrEmpty(gitRepoPath) ? FindGitRepositoryPath() : gitRepoPath;
        if (string.IsNullOrEmpty(repoPath))
        {
            UnityEngine.Debug.LogWarning("gitリポジトリが見つからなかったのでdiff取得できなかったよ～");
            return "";
        }

        ProcessStartInfo startInfo = new ProcessStartInfo()
        {
            FileName = "git",
            Arguments = "diff --unified=0",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process process = new Process() { StartInfo = startInfo };
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var sb = new StringBuilder();
        string currentFile = "";

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("diff --git"))
            {
                var parts = line.Split(' ');
                if (parts.Length > 2)
                    currentFile = parts[2].Replace("a/", "");
                sb.AppendLine($"\n# {currentFile}"); // ファイル名は見出しっぽく残す
            }
            else if (line.StartsWith("+++ "))
            {
                string filePath = line.Replace("+++ b/", "").Trim();
                sb.AppendLine($"+ {filePath}");
            }
            else if (line.StartsWith("--- "))
            {
                string filePath = line.Replace("--- a/", "").Trim();
                sb.AppendLine($"- {filePath}");
            }
            else if (line.StartsWith("+") || line.StartsWith("-"))
            {
                if (!line.StartsWith("+++") && !line.StartsWith("---"))
                    sb.AppendLine(line.Trim());
            }
        }

        return sb.ToString().Trim();
    }



    public static async void CommitChanges(string[] modifiedFiles)
    {
        if (modifiedFiles == null || modifiedFiles.Length == 0)
        {
            UnityEngine.Debug.Log("No files to commit.");
            return;
        }
        if (isCommitInProgress)
        {
            UnityEngine.Debug.LogWarning("すでにコミット中だよ～。終わるまで待ってね！");
            return;
        }
        isCommitInProgress = true;

        string gitDiff = GetGitDiff();
        string commitPrompt;
        string contextText;
        bool useDiff = gitDiff.Length < MaxGeminiInputLength; // しきい値超えたらファイル一覧のみ
        if (useDiff)
        {
            commitPrompt = commitMessage + "（下記は変更差分です。要約してコミットメッセージを生成してください）";
            contextText = gitDiff;
        }
        else
        {
            commitPrompt = commitMessage + "（下記は変更ファイル一覧です。内容を要約してコミットメッセージを生成してください）";
            contextText = string.Join("\n", modifiedFiles);
            UnityEngine.Debug.LogWarning("git diffが長すぎたのでファイル一覧のみGeminiに送るよ～");
        }

        string generatedCommitMessage = await GenerateCommitMessageAsync(commitPrompt, contextText);

        if (!string.IsNullOrEmpty(generatedCommitMessage) && !generatedCommitMessage.StartsWith("Error"))
        {
            UnityEngine.Debug.Log($"Generated commit message: {generatedCommitMessage}");
            await CommitChangesAsync(generatedCommitMessage);
        }
        else
        {
            UnityEngine.Debug.LogWarning("コミットメッセージ生成に失敗したよ…自動コミットは見送るね");
        }
        isCommitInProgress = false;
    }

    private static async Task CommitChangesAsync(string commitMessage)
    {
        UnityEngine.Debug.Log("Git commit started...");
        string repoPath = string.IsNullOrEmpty(gitRepoPath) ? FindGitRepositoryPath() : gitRepoPath;
        if (string.IsNullOrEmpty(repoPath))
        {
            UnityEngine.Debug.LogWarning("gitリポジトリが見つからなかったのでコミットできないよ～");
            return;
        }
        bool success = await Task.Run(() =>
        {
            try
            {
                RunGitCommand("git add .", repoPath);
                RunGitCommand($"git commit -m \"{commitMessage}\"", repoPath);
                return true;
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"Git operation failed: {ex.Message}");
                return false;
            }
        });

        if (success)
        {
            UnityEngine.Debug.Log("Git commit completed successfully!");
        }
        else
        {
            UnityEngine.Debug.LogError("Git commit failed.");
        }
    }

    // prompt, context で分岐するように
    private static async Task<string> GenerateCommitMessageAsync(string prompt, string context)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            UnityEngine.Debug.LogWarning("Gemini API Keyが未設定だよ～");
            return "Error: Gemini API Key not set.";
        }
        using (HttpClient client = new HttpClient())
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash-latest:generateContent?key={apiKey}";
            var payload = new
            {
                contents = new[] {
                    new {
                        parts = new[] {
                            new { text = $"{prompt}\n\n{context}\n\n" }
                        }
                    }
                }
            };

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            try
            {
                var response = await client.PostAsync(url, content);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    UnityEngine.Debug.LogError($"Gemini API Error: {response.StatusCode}\n{responseText}");
                    return $"Error: {response.StatusCode}";
                }

                dynamic result = JsonConvert.DeserializeObject(responseText);
                string generated = result.candidates[0].content.parts[0].text.ToString().Trim();
                return generated;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Exception: {ex.Message}");
                return "Error: Exception occurred.";
            }
        }
    }

    private static void RunGitCommand(string command, string repoPath)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/C cd \"{repoPath}\" && {command}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi);
        proc.WaitForExit();
        UnityEngine.Debug.Log($"Ran: {command}");
    }

    // 変更回数管理（メモリのみ）
    public static void IncrementModificationCount()
    {
        modificationCount++;
    }
    public static void ResetModificationCount()
    {
        modificationCount = 0;
    }
    public static int GetModificationCount()
    {
        return modificationCount;
    }
    public static int GetModificationCountThreshold()
    {
        return modificationCountThreshold;
    }

    [Serializable]
    private class Settings
    {
        public string apiKey;
        public string gitRepoPath;
        public string commitMessage;
        public int modificationCountThreshold;
    }
}
