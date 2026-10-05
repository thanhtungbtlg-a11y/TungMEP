using System;
using System.IO;

namespace TungMEP.Modules.Productivity
{
    internal static class ProductivityToolHistoryStore
    {
        private const string SettingsDirectoryName = "TungMEP";
        private const string FileName = "LastCommand.txt";

        public static string Load()
        {
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                {
                    return string.Empty;
                }
                string commandTypeName = File.ReadAllText(path).Trim();
                return IsAllowed(commandTypeName) ? commandTypeName : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void Save(string commandTypeName)
        {
            if (!IsAllowed(commandTypeName))
            {
                return;
            }
            try
            {
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, commandTypeName.Trim());
            }
            catch
            {
                // Last-command history must never block a productivity tool.
            }
        }

        private static bool IsAllowed(string commandTypeName)
        {
            return !string.IsNullOrWhiteSpace(commandTypeName) &&
                commandTypeName.StartsWith(
                    "TungMEP.Modules.",
                    StringComparison.Ordinal) &&
                commandTypeName.IndexOfAny(new[] { '\r', '\n' }) < 0;
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SettingsDirectoryName,
                FileName);
        }
    }
}
