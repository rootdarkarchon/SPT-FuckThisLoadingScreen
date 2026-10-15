using System;
using System.IO;
using UnityEditor;

public static class LoadingTimerValidation
{
    public static void Run()
    {
        string project = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        try
        {
            Program.Main(new[] { File.ReadAllText(Path.Combine(project, "install.txt")).Trim() }).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(project, "result.txt"), "PASS: full Streets scene coverage and failure reasons, lifecycle nesting/attribution/caps, subscriber snapshot/order/duplicates/exception identity/caps, all 963 installed lifecycle hooks and scene/activation patch compilation, EFT/Fika signatures.\n");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(project, "result.txt"), "FAIL: " + error);
            UnityEngine.Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }
}
