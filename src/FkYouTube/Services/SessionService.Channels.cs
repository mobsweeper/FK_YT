using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FkYouTube.Services;

public sealed partial class SessionService
{
    private string _selectedChannelId="";
    public YouTubeChannel? SelectedChannel {get;private set;}
    private sealed record ChannelSession(YouTubeChannel Channel,string Cookies);
    private string ChannelPath(string id)
    {
        var path=Path.Combine(_paths.Data,"channels",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))+".dpapi");
        FileBoundary.EnsureRegularPath(path);
        return path;
    }
    private ChannelSession ReadChannelSession()
    {
        byte[]? encrypted=null;byte[]? bytes=null;
        try
        {
            encrypted=FileBoundary.ReadBoundedFile(ChannelPath(_selectedChannelId),MaximumEncryptedSessionBytes);
            bytes=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);
            var saved=JsonSerializer.Deserialize<ChannelSession>(bytes,JsonDefaults.Options);
            if(saved?.Channel is null || saved.Channel.Id!=_selectedChannelId || string.IsNullOrWhiteSpace(saved.Cookies))
                throw new InvalidDataException(UiText.T("채널 세션이 일치하지 않습니다. 채널 목록을 갱신하세요.","The channel session does not match. Refresh the channel list.","チャンネルのセッションが一致しません。チャンネル一覧を更新してください。"));
            YouTubeAccountOptions.Validate(saved.Channel);
            return saved with{Cookies=FilterCookies(saved.Cookies)};
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {throw new InvalidDataException(UiText.T("선택한 채널 세션을 열 수 없습니다. 채널 목록을 갱신하고 다시 선택하세요.","The selected channel session could not be opened. Refresh the channel list and select it again.","選択したチャンネルのセッションを開けません。チャンネル一覧を更新してから、もう一度選択してください。"),ex);}
        finally
        {
            if(encrypted is not null)CryptographicOperations.ZeroMemory(encrypted);
            if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);
        }
    }
    public void RestoreSelectedChannel(string id)
    {
        if(HasSessionRecoveryFailure)throw new InvalidDataException(UiText.T("이전 로그인 변경의 복구가 필요합니다. 새 로그인 연결을 확정하세요.","The previous sign-in change requires recovery. Confirm a new sign-in connection.","前回のログイン変更の復旧が必要です。新しいログイン接続を確定してください。"));
        _selectedChannelId=id;SelectedChannel=null;
        if(id.Length>0)SelectedChannel=ReadChannelSession().Channel;
    }
    public void ForgetSelectedChannel(){_selectedChannelId="";SelectedChannel=null;}
    private CookieLease AcquireSelected()
    {
        EnsureSessionUsable();
        var saved=ReadChannelSession();
        var bytes=Encoding.UTF8.GetBytes(saved.Cookies);
        try{return CreateCookieFile(stream=>stream.Write(bytes));}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private async Task<JsonElement> ChannelRequestAsync(object request,CancellationToken ct,params CookieLease[] leases)
    {
        var receipt=new ProcessStopReceipt();foreach(var lease in leases)lease.AttachConsumer(receipt);
        var response=await ProcessRunner.RunAsync(_paths.Python,["-B",Path.Combine(_paths.Root,"worker","youtube_session.py")],_paths.Root,TimeSpan.FromSeconds(60),ct,JsonSerializer.Serialize(request,JsonDefaults.Options),receipt);
        if(response.ExitCode!=0)throw new IOException(UiText.T("YouTube 채널 정보를 가져오지 못했습니다. 로그인 연결을 갱신하세요.","Could not retrieve YouTube channel information. Refresh your sign-in connection.","YouTube チャンネル情報を取得できませんでした。ログイン接続を更新してください。"));
        return ParseChannelResponse(response.Output);
    }
    private static JsonElement ParseChannelResponse(string output)
    {
        try
        {
            using var doc=JsonDocument.Parse(output);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object || !doc.RootElement.TryGetProperty("type",out var type) ||
                type.ValueKind!=JsonValueKind.String)
                throw new InvalidDataException(UiText.T("YouTube 채널 응답 형식이 올바르지 않습니다. 채널 목록을 갱신하세요.","The YouTube channel response is invalid. Refresh the channel list.","YouTube チャンネルの応答形式が正しくありません。チャンネル一覧を更新してください。"));
            if(type.GetString()=="error")
            {
                if(doc.RootElement.TryGetProperty("code",out var code) && code.ValueKind==JsonValueKind.String && code.GetString()=="unsupported")
                    throw new InvalidDataException(UiText.T("YouTube 채널 목록 형식을 읽지 못했습니다. 잠시 후 채널 목록을 갱신하거나 앱 업데이트를 확인하세요.","Could not read the YouTube channel list format. Refresh the channel list later or check for an app update.","YouTube チャンネル一覧の形式を読み取れませんでした。しばらくしてからチャンネル一覧を更新するか、アプリの更新を確認してください。"));
                throw new InvalidDataException(UiText.T("선택한 YouTube 채널을 확인하지 못했습니다. 로그인 연결 또는 채널 목록을 갱신하세요.","Could not verify the selected YouTube channel. Refresh your sign-in connection or channel list.","選択した YouTube チャンネルを確認できませんでした。ログイン接続またはチャンネル一覧を更新してください。"));
            }
            return doc.RootElement.Clone();
        }
        catch(JsonException ex){throw new InvalidDataException(UiText.T("YouTube 채널 응답 형식이 올바르지 않습니다. 채널 목록을 갱신하세요.","The YouTube channel response is invalid. Refresh the channel list.","YouTube チャンネルの応答形式が正しくありません。チャンネル一覧を更新してください。"),ex);}
    }
    public async Task<IReadOnlyList<YouTubeChannel>> ListChannelsAsync(CancellationToken ct)
    {
        using var lease=AcquireBase();
        var result=await ChannelRequestAsync(new{command="account.list",requestId=Guid.NewGuid().ToString("N"),cookiePath=lease.Path},ct,lease);
        if(!result.TryGetProperty("channels",out var channels) || channels.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException(UiText.T("YouTube 채널 목록 형식이 올바르지 않습니다. 채널 목록을 갱신하세요.","The YouTube channel list format is invalid. Refresh the channel list.","YouTube チャンネル一覧の形式が正しくありません。チャンネル一覧を更新してください。"));
        try
        {
            var items=channels.Deserialize<List<YouTubeChannel>>(JsonDefaults.Options)??[];
            if(items.Any(item=>item is null))throw new InvalidDataException(UiText.T("YouTube 채널 목록 형식이 올바르지 않습니다. 채널 목록을 갱신하세요.","The YouTube channel list format is invalid. Refresh the channel list.","YouTube チャンネル一覧の形式が正しくありません。チャンネル一覧を更新してください。"));
            return items;
        }
        catch(JsonException ex){throw new InvalidDataException(UiText.T("YouTube 채널 목록 형식이 올바르지 않습니다. 채널 목록을 갱신하세요.","The YouTube channel list format is invalid. Refresh the channel list.","YouTube チャンネル一覧の形式が正しくありません。チャンネル一覧を更新してください。"),ex);}
    }
    public async Task SelectChannelAsync(YouTubeChannel channel,CancellationToken ct)
    {
        EnsureSessionUsable();
        FileBoundary.EnsureRegularPath(Path.Combine(_paths.Data,"temp"));
        using var source=AcquireBase();using var output=CreateCookieLease(Path.Combine(_paths.Data,"temp","cookies-"+Guid.NewGuid().ToString("N")+".txt"));
        var result=await ChannelRequestAsync(new{command="account.select",requestId=Guid.NewGuid().ToString("N"),cookiePath=source.Path,accountContext=channel,outputCookiePath=output.Path},ct,source,output);
        ct.ThrowIfCancellationRequested();
        if(!result.TryGetProperty("verified",out var verified) || verified.ValueKind!=JsonValueKind.True ||
            !result.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.String || id.GetString()!=channel.Id ||
            !result.TryGetProperty("dataSyncId",out var dataSyncId) || dataSyncId.ValueKind!=JsonValueKind.String || dataSyncId.GetString()!=channel.DataSyncId ||
            !result.TryGetProperty("authUser",out var authUser) || authUser.ValueKind!=JsonValueKind.Number || !authUser.TryGetInt32(out var actualUser) || actualUser!=channel.AuthUser)
            throw new InvalidDataException(UiText.T("선택한 YouTube 채널과 세션이 일치하지 않습니다.","The selected YouTube channel does not match the session.","選択した YouTube チャンネルとセッションが一致しません。"));
        var saved=new ChannelSession(channel with{SigninUrl=null},FilterCookies(ReadCookieText(output.Path!)));
        var bytes=JsonSerializer.SerializeToUtf8Bytes(saved,JsonDefaults.Options);
        try
        {
            var path=ChannelPath(channel.Id);
            var encrypted=ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
            StoreEncryptedFile(path,encrypted);
            _selectedChannelId=channel.Id;SelectedChannel=saved.Channel;
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
}
