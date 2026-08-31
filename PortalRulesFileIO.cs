using System;
using System.IO;
using System.Text;

namespace PortalRules;

internal static class PortalRulesFileIO
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static void WriteAtomicFile(
        string path,
        string content,
        string? backupPath)
    {
        string directory = Path.GetDirectoryName(path) ??
                           throw new InvalidOperationException(
                               "Target directory is unavailable.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            "." + Path.GetFileName(path) + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (StreamWriter writer = new(stream, Utf8WithoutBom))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(
                        temporaryPath,
                        path,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceUsingMoves(temporaryPath, path, backupPath);
                }
                catch (NotSupportedException)
                {
                    ReplaceUsingMoves(temporaryPath, path, backupPath);
                }
                catch (IOException) when (Path.DirectorySeparatorChar == '/')
                {
                    ReplaceUsingMoves(temporaryPath, path, backupPath);
                }
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ReplaceUsingMoves(
        string temporaryPath,
        string path,
        string? backupPath)
    {
        string recoverablePath = backupPath ?? path + ".replace-backup";
        if (File.Exists(recoverablePath))
        {
            File.Delete(recoverablePath);
        }

        File.Move(path, recoverablePath);
        try
        {
            File.Move(temporaryPath, path);
            if (backupPath == null && File.Exists(recoverablePath))
            {
                File.Delete(recoverablePath);
            }
        }
        catch
        {
            if (!File.Exists(path) && File.Exists(recoverablePath))
            {
                File.Move(recoverablePath, path);
            }

            throw;
        }
    }
}
