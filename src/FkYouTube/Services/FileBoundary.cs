using System.Security.Cryptography;

namespace FkYouTube.Services;

// Used only for app-owned work paths and downloaded sources. User-selected
// destination folders may intentionally be reached through a junction.
internal static class FileBoundary
{
    internal static byte[] ReadBoundedFile(string path,int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        EnsureRegularPath(path);
        // The length decision and bytes come from the same open file. Sharing
        // permits other readers while denying writers and replacement/deletion.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.SequentialScan);
        EnsureRegularPath(path);
        var length=stream.Length;
        if(length>maximumBytes)throw FileTooLarge();
        var bytes=new byte[checked((int)length)];
        try
        {
            stream.ReadExactly(bytes);
            // Retain a read bound even if a backing filesystem permits changes
            // despite the sharing mode; never allocate from a second path open.
            if(stream.ReadByte()!=-1)throw FileTooLarge();
            return bytes;
        }
        catch{CryptographicOperations.ZeroMemory(bytes);throw;}
    }
    private static InvalidDataException FileTooLarge()=>new(UiText.T(
        "저장된 파일이 너무 큽니다. 원본 파일을 확인해 주세요.",
        "The stored file is too large. Check the original file.",
        "保存されたファイルが大きすぎます。元のファイルを確認してください。"));
    internal static void EnsureRegularPath(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current);
            current = Path.GetDirectoryName(current))
            EnsureRegularEntry(current);
    }
    internal static void EnsureRegularFileAndSiblings(string path,params string[] suffixes)
    {
        var full=Path.GetFullPath(path);
        EnsureRegularPath(Path.GetDirectoryName(full)!);
        EnsureRegularEntry(full);
        foreach(var suffix in suffixes)
        {
            if(suffix.IndexOfAny([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar])>=0)
                throw new ArgumentException("A sibling suffix cannot contain a path separator.",nameof(suffixes));
            EnsureRegularEntry(full+suffix);
        }
    }
    private static void EnsureRegularEntry(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.T("앱 작업 경로에 연결된 파일 또는 폴더가 있습니다. 경로를 확인해 주세요.","The app working path contains a linked file or folder. Check the path.","アプリの作業パスにリンクされたファイルまたはフォルダーがあります。パスを確認してください。"));
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
