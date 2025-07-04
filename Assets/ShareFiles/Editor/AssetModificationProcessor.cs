using UnityEditor;
using UnityEngine;
using System.Linq;

public class AssetModificationProcessor : AssetPostprocessor
{
    private static bool isCommitInProgress = false;

    public static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        if (isCommitInProgress) return;

        string[] modifiedFiles = AutoCommitter.GetModifiedFiles();
        if (modifiedFiles.Any())
        {
            // 変更回数を1増やす！
            AutoCommitter.IncrementModificationCount();
            int now = AutoCommitter.GetModificationCount();
            int threshold = AutoCommitter.GetModificationCountThreshold();
            UnityEngine.Debug.Log($"変更回数: {now} / {threshold}");

            if (now >= threshold)
            {
                UnityEngine.Debug.Log("閾値に到達！コミットします～");
                isCommitInProgress = true;
                AutoCommitter.CommitChanges(modifiedFiles);
                AutoCommitter.ResetModificationCount();
                isCommitInProgress = false;
            }
        }
        else
        {
            UnityEngine.Debug.Log("No changes detected.");
        }
    }
}
