using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FkYouTube.Services;

namespace FkYouTube;

public partial class MainWindow
{
    private void RefreshBrowseUiText()
    {
        if(_channelReloadError is not null && _channelReloadErrorUiText is {} errorText)_channelReloadError=errorText();
        BrowseFiltersScroll.ToolTip=_source=="home"?UiText.T("YouTube 홈 추천 필터","YouTube Home filters","YouTube ホームのフィルター"):UiText.T("YouTube 목록 필터","YouTube list filters","YouTube 一覧のフィルター");
        if(_current is {} item)
            PlayingChannelName.Text=string.IsNullOrWhiteSpace(item.Channel)?UiText.T("채널 정보 확인 중","Checking channel information","チャンネル情報を確認中"):item.Channel;
    }

    private void ResetBrowseMetadata(string source)
    {
        ChannelHeader.Visibility=Visibility.Collapsed;
        ChannelHeaderAvatar.Url=null;
        ChannelHeaderTitle.Text="";
        ChannelHeaderDescription.Text="";
        BrowseFilterItems.ItemsSource=null;
        BrowseFiltersScroll.Visibility=Visibility.Collapsed;
        BrowseFiltersScroll.ScrollToHorizontalOffset(0);
        BrowseFiltersScroll.ToolTip=source=="home"?UiText.T("YouTube 홈 추천 필터","YouTube Home filters","YouTube ホームのフィルター"):UiText.T("YouTube 목록 필터","YouTube list filters","YouTube 一覧のフィルター");
    }

    private void ApplyBrowseMetadata(FeedNavigationMetadata metadata)
    {
        if(!string.IsNullOrWhiteSpace(metadata.Title))SetPageUiText(metadata.Title);
        var channel=metadata.Channel;
        ChannelHeader.Visibility=channel==null?Visibility.Collapsed:Visibility.Visible;
        ChannelHeaderAvatar.Url=channel?.Thumbnail;
        ChannelHeaderTitle.Text=channel?.Title??"";
        ChannelHeaderDescription.Text=channel?.Description??"";
        ChannelHeaderDescription.Visibility=string.IsNullOrWhiteSpace(channel?.Description)?Visibility.Collapsed:Visibility.Visible;
        BrowseFilterItems.ItemsSource=metadata.Filters;
        BrowseFiltersScroll.Visibility=metadata.Filters.Count>0?Visibility.Visible:Visibility.Collapsed;
    }

    private void UpdatePlayingChannel(VideoItem item)
    {
        PlayingChannelButton.Tag=item;
        PlayingChannelButton.IsEnabled=!AccountWorkBlocked && item.CanOpenChannel;
        PlayingChannelAvatar.Url=item.ChannelThumbnail;
        PlayingChannelName.Text=string.IsNullOrWhiteSpace(item.Channel)?UiText.T("채널 정보 확인 중","Checking channel information","チャンネル情報を確認中"):item.Channel;
    }

    private async void Channel_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || sender is not Button{Tag:VideoItem item})return;
        if(!YouTubeService.TryChannelUrl(item.ChannelUrl,out var url) &&
           (string.IsNullOrWhiteSpace(item.ChannelId) || !YouTubeService.TryChannelUrl("https://www.youtube.com/channel/"+item.ChannelId,out url)))return;
        e.Handled=true;
        var view=++_viewGeneration;
        if(!await StopPlaybackAsync())return;
        if(AccountWorkBlocked || view!=_viewGeneration)return;
        await OpenFeedAsync(url,string.IsNullOrWhiteSpace(item.Channel)?UiText.T("채널 영상","Channel videos","チャンネルの動画"):UiText.F("{0} · 영상","{0} · Videos","{0} · 動画",item.Channel));
    }

    private async void BrowseFilter_Click(object sender,RoutedEventArgs e)
    {
        if(AccountWorkBlocked || sender is not Button{Tag:FeedFilter filter} || filter.Selected || string.IsNullOrWhiteSpace(filter.Id))return;
        e.Handled=true;
        await SelectFeedFilterAsync(filter.Id);
    }

    private void BrowseFilters_PreviewMouseWheel(object sender,MouseWheelEventArgs e)
    {
        if(sender is not ScrollViewer scroll || scroll.ScrollableWidth<=0)return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset-e.Delta);
        e.Handled=true;
    }
}

public sealed class VideoCardDetailConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
    {
        if(value is not VideoItem item)return "";
        return item.LocalDetailFactory?.Invoke()??item.LocalDetail??(item.ViewCount is { } views?UiText.F("조회 {0:N0}회","{0:N0} views","{0:N0} 回視聴",views):"");
    }

    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}

public sealed class ChannelNameConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>
        value is string name && !string.IsNullOrWhiteSpace(name)?name:UiText.T("채널 정보 없음","No channel information","チャンネル情報なし");

    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
