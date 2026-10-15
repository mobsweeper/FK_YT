"""Offline channel server-sort fixtures; never read cookies or contact YouTube."""

import copy
import unittest
from types import SimpleNamespace

from yt_dlp.extractor.youtube import YoutubeTabIE

from channel_feed import ChannelFeed, ChannelFeedError, channel_videos_url


CHANNEL = "UC" + "x" * 22
OTHER_CHANNEL = "UC" + "y" * 22
A, B, C, D = "AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC", "DDDDDDDDDDD"
TARGET = "browse-feed" + CHANNEL
ACCOUNT = {"id": "fixture", "pageId": "123", "dataSyncId": "123||user", "authUser": 1}


def endpoint(token):
    return {"commandMetadata": {"webCommandMetadata": {"apiUrl": "/youtubei/v1/browse", "sendPost": True}},
            "continuationCommand": {"token": token, "request": "CONTINUATION_REQUEST_TYPE_BROWSE"}}


def chips(*, oldest=True, selected="Latest", selected_has_endpoint=False):
    result = []
    for label in ("Latest", "Popular", "Oldest") if oldest else ("Latest", "Popular"):
        value = {"text": {"simpleText": label}, "isSelected": label == selected}
        if label != selected or selected_has_endpoint:
            value["navigationEndpoint"] = endpoint("sort-" + label.lower())
        result.append({"chipCloudChipRenderer": value})
    return {"feedFilterChipBarRenderer": {"contents": result}}


def video(video_id, *, byline=False, members=False):
    value = {"videoId": video_id, "title": {"simpleText": "Fixture " + video_id},
             "lengthText": {"simpleText": "2:03"}, "viewCountText": {"simpleText": "1,234 views"},
             "thumbnail": {"thumbnails": [{"url": "https://i.ytimg.com/vi/" + video_id + "/hqdefault.jpg", "width": 480}]}}
    if byline:
        value["shortBylineText"] = {"runs": [{"text": "Other owner", "navigationEndpoint": {
            "browseEndpoint": {"browseId": OTHER_CHANNEL}}}]}
    if members:
        value["badges"] = [{"metadataBadgeRenderer": {"style": "BADGE_STYLE_TYPE_MEMBERS_ONLY"}}]
    return {"richItemRenderer": {"content": {"videoRenderer": value}}}


def continuation(token):
    return {"continuationItemRenderer": {"continuationEndpoint": endpoint(token)}}


def initial(ids=(A, B), *, token="latest-page2", header=None, owner=CHANNEL):
    values = [video(value) for value in ids] + ([continuation(token)] if token else [])
    return {"metadata": {"channelMetadataRenderer": {
        "externalId": owner, "title": "Fixture owner", "description": "Channel description",
        "avatar": {"thumbnails": [{"url": "https://yt3.ggpht.com/fixture-avatar", "width": 176}]}}},
        "header": {"c4TabbedHeaderRenderer": {"banner": {"thumbnails": [{"url": "https://fixture/banner"}]}}},
        "contents": {"twoColumnBrowseResultsRenderer": {"tabs": [{"tabRenderer": {
            "selected": True, "title": "Videos", "tabIdentifier": "videos", "content": {
                "richGridRenderer": {"header": header if header is not None else chips(), "contents": values}}}}]}}}


def update(ids, *, token=None, target=TARGET, header=None):
    actions = [{"reloadContinuationItemsCommand": {"targetId": target,
        "continuationItems": [*[video(value) for value in ids], *([continuation(token)] if token else [])]}}]
    if header:
        actions.append({"reloadContinuationItemsCommand": {"targetId": "feed-filter-chip-bar", "continuationItems": [header]}})
    return {"onResponseReceivedActions": actions}


class FakeExtractor(YoutubeTabIE):
    def __init__(self, ydl, responses, *, config=None, authenticated=True):
        super().__init__(ydl)
        self.responses = list(responses)
        self.calls = []
        self.config_fixture = config or {"INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}}, "LOGGED_IN": True}
        self.authenticated_fixture = authenticated
        self.config_calls = 0

    @property
    def is_authenticated(self):
        return self.authenticated_fixture

    def initialize(self):
        pass

    def _download_ytcfg(self, client, item):
        self.config_calls += 1
        return copy.deepcopy(self.config_fixture)

    def generate_api_headers(self, **kwargs):
        self.header_arguments = copy.deepcopy(kwargs)
        return {"Authorization": "synthetic-only"}

    def _extract_context(self, config):
        return copy.deepcopy(config["INNERTUBE_CONTEXT"])

    def _call_api(self, endpoint, query, item, **kwargs):
        self.calls.append((endpoint, copy.deepcopy(query), copy.deepcopy(kwargs)))
        if not self.responses:
            raise AssertionError("Unexpected API request")
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


class FakeYdl:
    class Styles:
        EMPHASIS = ""

    def __init__(self, responses, **kwargs):
        self.params = {}
        self.extractor = FakeExtractor(self, responses, **kwargs)

    def get_info_extractor(self, name):
        if name != "YoutubeTab":
            raise AssertionError("Per-video extraction prohibited")
        return self.extractor

    def report_warning(self, *_args, **_kwargs):
        pass

    def write_debug(self, *_args, **_kwargs):
        pass

    def _format_err(self, text, *_args, **_kwargs):
        return text


def feed_with(*responses, first=None, resolved=None, account=None, **kwargs):
    resolved = resolved or {"endpoint": {"browseEndpoint": {"browseId": CHANNEL, "params": "actual-videos-tab"}}}
    ydl = FakeYdl([resolved, first or initial(), *responses], **kwargs)
    return ChannelFeed(ydl, "https://www.youtube.com/@fixture/videos", account), ydl.extractor


class ChannelFeedTests(unittest.TestCase):
    def test_full_bounded_page_preserves_continuation_after_last_card(self):
        ids = [f"{index:011d}" for index in range(256)]
        feed, extractor = feed_with(update([C]), first=initial(ids))
        feed.initialize()
        self.assertEqual([next(feed)["id"] for _ in ids], ids)
        self.assertEqual(next(feed)["id"], C)
        self.assertEqual(extractor.calls[-1][1], {"continuation": "latest-page2"})

    def test_oversized_page_reports_error_instead_of_silent_end(self):
        ids = [f"{index:011d}" for index in range(257)]
        feed, _ = feed_with(first=initial(ids))
        with self.assertRaises(ChannelFeedError):
            feed.initialize()

    def test_urls_canonicalized_and_invalid_sources_rejected(self):
        for value in ("youtube.com/@fixture", "http://m.youtube.com/@fixture/videos?sort=da", "https://www.youtube.com/c/name", "https://www.youtube.com/user/name", "https://www.youtube.com/channel/" + CHANNEL):
            self.assertTrue(channel_videos_url(value).startswith("https://www.youtube.com/"))
            self.assertTrue(channel_videos_url(value).endswith("/videos"))
        for value in ("https://youtube.com/watch?v=" + A, "https://youtube.com/@fixture/shorts", "https://youtube.com/channel/UCbad", "https://evil.invalid/@fixture", "https://youtube.com/@fixture%2Fvideos", "https://u:p@youtube.com/@fixture", "https://youtube.com:666/@fixture", "https://youtube.com/@fixture/videos/other"):
            with self.subTest(value=value), self.assertRaises(ChannelFeedError):
                channel_videos_url(value)

    def test_initialize_emits_same_channel_header_before_lazy_continuation(self):
        feed, extractor = feed_with(update([C]))
        self.assertEqual(extractor.calls, [])
        metadata = feed.initialize()
        self.assertEqual(len(extractor.calls), 2)
        self.assertEqual(metadata["channel"], {"id": CHANNEL, "title": "Fixture owner", "url": "https://www.youtube.com/channel/" + CHANNEL + "/videos",
            "thumbnail": "https://yt3.ggpht.com/fixture-avatar", "description": "Channel description"})
        self.assertEqual(metadata["filterKind"], "channel-sort")
        self.assertEqual([value["label"] for value in metadata["filters"]], ["Latest", "Popular", "Oldest"])
        raw = next(feed)
        self.assertEqual((raw["channel"], raw["channel_id"], raw["channel_url"], raw["channel_thumbnail"]), ("Fixture owner", CHANNEL, metadata["channel"]["url"], metadata["channel"]["thumbnail"]))
        self.assertEqual(raw["duration"], 123)
        self.assertEqual(raw["view_count"], 1234)
        self.assertEqual([value["id"] for value in feed], [B, C])
        self.assertEqual(extractor.calls[-1][1], {"continuation": "latest-page2"})
        self.assertEqual([value[0] for value in extractor.calls], ["navigation/resolve_url", "browse", "browse"])
        self.assertNotIn("sort-oldest", repr(metadata))

    def test_server_sort_popular_oldest_latest_and_continuations_differ(self):
        feed, extractor = feed_with(update([B], token="popular-page2"), update([C]),
            update([D], token="oldest-page2"), update([A]), initial((A, B), token=None))
        filters = {value["label"]: value["id"] for value in feed.initialize()["filters"]}
        self.assertEqual(next(feed)["id"], A)
        feed.select_filter(filters["Popular"])
        self.assertEqual([value["id"] for value in feed], [B, C])
        filters = {value["label"]: value["id"] for value in feed.metadata["filters"]}
        feed.select_filter(filters["Oldest"])
        self.assertEqual([value["id"] for value in feed], [D, A])
        filters = {value["label"]: value["id"] for value in feed.metadata["filters"]}
        feed.select_filter(filters["Latest"])
        self.assertEqual([value["id"] for value in feed], [A, B])
        self.assertEqual([value[1] for value in extractor.calls[2:]], [
            {"continuation": "sort-popular"}, {"continuation": "popular-page2"},
            {"continuation": "sort-oldest"}, {"continuation": "oldest-page2"},
            {"browseId": CHANNEL, "params": "actual-videos-tab"}])

    def test_unoffered_oldest_is_never_synthesized_and_bad_handle_is_inert(self):
        feed, extractor = feed_with(first=initial(header=chips(oldest=False)))
        self.assertEqual([value["label"] for value in feed.initialize()["filters"]], ["Latest", "Popular"])
        for handle in ("oldest", "sort-popular", "fake", None):
            with self.assertRaises(ChannelFeedError):
                feed.select_filter(handle)
        self.assertEqual(len(extractor.calls), 2)

    def test_malformed_and_write_endpoints_not_offered(self):
        header = chips()
        header["feedFilterChipBarRenderer"]["contents"][1]["chipCloudChipRenderer"]["navigationEndpoint"]["feedbackEndpoint"] = {"feedbackToken": "never-send"}
        header["feedFilterChipBarRenderer"]["contents"][2]["chipCloudChipRenderer"]["navigationEndpoint"]["commandMetadata"]["webCommandMetadata"]["apiUrl"] = "/youtubei/v1/feedback"
        feed, _ = feed_with(first=initial(header=header))
        self.assertEqual([value["label"] for value in feed.initialize()["filters"]], ["Latest"])
        for resolved in ({"endpoint": {"watchEndpoint": {"videoId": A}}}, {"endpoint": {"browseEndpoint": {"browseId": "FEwhat_to_watch"}}}, {"endpoint": {"browseEndpoint": {"browseId": CHANNEL}, "feedbackEndpoint": {}}}):
            feed, extractor = feed_with(resolved=resolved)
            with self.assertRaises(ChannelFeedError):
                feed.initialize()
            self.assertEqual(len(extractor.calls), 1)

    def test_selected_account_headers_body_and_logout(self):
        config = {"LOGGED_IN": True, "DELEGATED_SESSION_ID": "old", "USER_SESSION_ID": "old", "INNERTUBE_CONTEXT": {
            "client": {"clientName": "WEB"}, "user": {"onBehalfOfUser": "old"}}}
        feed, extractor = feed_with(account=ACCOUNT, config=config)
        feed.initialize()
        self.assertEqual(extractor.header_arguments["delegated_session_id"], "123")
        self.assertEqual(extractor.header_arguments["user_session_id"], "user")
        self.assertEqual(extractor.header_arguments["session_index"], 1)
        self.assertTrue(all(value[2]["context"]["user"]["onBehalfOfUser"] == "123" for value in extractor.calls))
        response = initial()
        response["responseContext"] = {"mainAppWebResponseContext": {"loggedOut": True}}
        feed, _ = feed_with(first=response, account=ACCOUNT)
        with self.assertRaises(ChannelFeedError) as raised:
            feed.initialize()
        self.assertEqual(raised.exception.code, "auth")
        feed, extractor = feed_with(account=ACCOUNT, authenticated=False)
        with self.assertRaises(ChannelFeedError):
            feed.initialize()
        self.assertEqual(extractor.calls, [])

    def test_scope_rejects_other_tabs_surfaces_and_metadata_owner(self):
        response = initial()
        response["contents"]["twoColumnBrowseResultsRenderer"]["tabs"][0]["tabRenderer"]["tabIdentifier"] = "shorts"
        for first in (response, initial(owner=OTHER_CHANNEL)):
            feed, _ = feed_with(first=first)
            with self.assertRaises(ChannelFeedError):
                feed.initialize()
        feed, _ = feed_with(update([C], target="comments-section"))
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(next(feed)["id"], B)
        with self.assertRaises(ChannelFeedError):
            next(feed)

    def test_repeat_tokens_empty_pages_and_total_calls_bounded(self):
        feed, extractor = feed_with(update([B, C], token="latest-page2"))
        self.assertEqual([value["id"] for value in feed], [A, B, C])
        self.assertEqual(len(extractor.calls), 3)
        feed, extractor = feed_with(update([], token="empty2"), update([], token="empty3"), update([], token="empty4"))
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(next(feed)["id"], B)
        with self.assertRaises(ChannelFeedError):
            next(feed)
        self.assertEqual(len(extractor.calls), 5)
        feed, extractor = feed_with(update([C], token="next2"), update([D]))
        feed.max_pages = 1
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(next(feed)["id"], B)
        self.assertEqual(next(feed)["id"], C)
        with self.assertRaises(ChannelFeedError):
            next(feed)
        self.assertEqual(len(extractor.calls), 3)

    def test_selected_chip_update_keeps_its_actual_command(self):
        feed, extractor = feed_with(update([B], header=chips(selected="Popular")), update([C]))
        handle = next(value["id"] for value in feed.initialize()["filters"] if value["label"] == "Popular")
        metadata = feed.select_filter(handle)
        selected = next(value for value in metadata["filters"] if value["selected"])
        self.assertEqual(selected["label"], "Popular")
        feed.select_filter(selected["id"])
        self.assertEqual(extractor.calls[-1][1], {"continuation": "sort-popular"})

    def test_channel_sort_browse_endpoint_and_loading_placeholder(self):
        header = chips()
        popular = header["feedFilterChipBarRenderer"]["contents"][1]["chipCloudChipRenderer"]
        popular["navigationEndpoint"] = {"browseEndpoint": {"browseId": CHANNEL, "params": "server-popular"}}
        oldest = header["feedFilterChipBarRenderer"]["contents"][2]["chipCloudChipRenderer"]
        oldest["navigationEndpoint"]["continuationCommand"].update({"invalidationGroupId": "valid-group", "command": {
            "showReloadUiCommand": {"targetId": TARGET, "content": {"ghostGridViewModel": {"numRows": 2}}}}})
        feed, extractor = feed_with(update([B]), update([D]), first=initial(header=header))
        filters = {value["label"]: value["id"] for value in feed.initialize()["filters"]}
        feed.select_filter(filters["Popular"])
        filters = {value["label"]: value["id"] for value in feed.metadata["filters"]}
        feed.select_filter(filters["Oldest"])
        self.assertEqual(extractor.calls[-2][1], {"browseId": CHANNEL, "params": "server-popular"})
        self.assertEqual(extractor.calls[-1][1], {"continuation": "sort-oldest"})

    def test_server_selected_state_wins_and_handles_rotate_after_selection(self):
        feed, extractor = feed_with(update([B], header=chips(selected="Latest")), update([C]))
        metadata = feed.initialize()
        old = {value["label"]: value["id"] for value in metadata["filters"]}
        metadata = feed.select_filter(old["Popular"])
        self.assertEqual([value["label"] for value in metadata["filters"] if value["selected"]], ["Latest"])
        new = {value["label"]: value["id"] for value in metadata["filters"]}
        self.assertTrue(all(old[label] != new[label] for label in old))
        with self.assertRaises(ChannelFeedError):
            feed.select_filter(old["Popular"])
        feed.select_filter(new["Popular"])
        self.assertEqual([value["label"] for value in feed.metadata["filters"] if value["selected"]], ["Popular"])

    def test_new_chip_view_model_tap_command_and_paging_keep_handles(self):
        header = {"chipBarViewModel": {"chips": [
            {"chipViewModel": {"text": "Latest", "selected": True}},
            {"chipViewModel": {"text": "Popular", "tapCommand": endpoint("sort-popular")}}]}}
        feed, _ = feed_with(update([C]), first=initial(header=header))
        filters = feed.initialize()["filters"]
        self.assertEqual([value["label"] for value in filters], ["Latest", "Popular"])
        self.assertEqual([value["id"] for value in feed], [A, B, C])
        self.assertEqual(feed.metadata["filters"], filters)

    def test_current_server_uuid_grid_target_sort_reload_and_continuation(self):
        target = "6b13b555-0000-2963-b805-582429c1d3c4"
        values = []
        for label in ("Latest", "Popular", "Oldest"):
            command = endpoint("sort-" + label.lower())
            command["continuationCommand"]["command"] = {
                "clickTrackingParams": "presentation-tracking",
                "showReloadUiCommand": {"targetId": target}}
            values.append({"chipViewModel": {"text": label, "selected": label == "Latest",
                "tapCommand": {"innertubeCommand": command}}})
        first = initial(header={"chipBarViewModel": {"chips": values}})
        grid = first["contents"]["twoColumnBrowseResultsRenderer"]["tabs"][0]["tabRenderer"]["content"]["richGridRenderer"]
        grid["targetId"] = target
        feed, extractor = feed_with(update([C], target=target), update([D], token="oldest-page2", target=target),
            update([A], target=target), first=first)
        metadata = feed.initialize()
        self.assertEqual([value["label"] for value in metadata["filters"]], ["Latest", "Popular", "Oldest"])
        self.assertEqual([value["id"] for value in feed], [A, B, C])
        oldest = next(value["id"] for value in metadata["filters"] if value["label"] == "Oldest")
        feed.select_filter(oldest)
        self.assertEqual([value["id"] for value in feed], [D, A])
        self.assertEqual(extractor.calls[3][1], {"continuation": "sort-oldest"})
        self.assertEqual(extractor.calls[4][1], {"continuation": "oldest-page2"})

    def test_members_only_availability_retained_and_foreign_target_ignored(self):
        first = initial(token=None)
        grid = first["contents"]["twoColumnBrowseResultsRenderer"]["tabs"][0]["tabRenderer"]["content"]["richGridRenderer"]
        grid["contents"] = [video(A, members=True), video(B)]
        feed, _ = feed_with(first=first)
        self.assertEqual(next(feed)["availability"], "subscriber_only")
        self.assertNotEqual(next(feed).get("availability"), "subscriber_only")
        with self.assertRaises(StopIteration):
            next(feed)

    def test_worker_hides_channel_members_across_pages_and_server_sort(self):
        from feed_worker import FeedWorker
        first = initial(ids=(), token="next-page")
        grid = first["contents"]["twoColumnBrowseResultsRenderer"]["tabs"][0]["tabRenderer"]["content"]["richGridRenderer"]
        grid["contents"] = [video(A, members=True), video(B), continuation("next-page")]
        second, oldest = update([C]), update([B, A])
        second["onResponseReceivedActions"][0]["reloadContinuationItemsCommand"]["continuationItems"].append(video(D, members=True))
        oldest["onResponseReceivedActions"][0]["reloadContinuationItemsCommand"]["continuationItems"].insert(0, video(D, members=True))
        feed, extractor = feed_with(second, oldest, first=first)
        worker = FeedWorker(factory=lambda options: SimpleNamespace(close=lambda: None),
                            navigation_factory=lambda *args: feed)
        request = {"command": "feed.open", "requestId": "members", "generation": 1,
                   "source": "https://www.youtube.com/@fixture/videos", "batchSize": 2,
                   "hideMembersOnly": True}
        try:
            opened = list(worker.events(request))
            self.assertEqual([event["item"]["id"] for event in opened if event["type"] == "item"], [B, C])
            self.assertEqual(opened[-1]["count"], 2)
            self.assertEqual(opened[-1]["skippedMembersOnly"], 2)
            self.assertFalse(opened[-1]["hasMore"])
            handle = next(value["id"] for value in feed.metadata["filters"] if value["label"] == "Oldest")
            selected = list(worker.events({**request, "command": "feed.select", "generation": 2,
                                          "previousGeneration": 1, "filterId": handle}))
            self.assertEqual([event["item"]["id"] for event in selected if event["type"] == "item"], [B, A])
            self.assertEqual(selected[-1]["skippedMembersOnly"], 1)
            self.assertEqual(extractor.calls[-1][1], {"continuation": "sort-oldest"})
        finally:
            worker.close()

    def test_uuid_target_replaced_on_full_selected_videos_response(self):
        first, second = initial(), initial((D,), token="after-sort")
        first_grid = first["contents"]["twoColumnBrowseResultsRenderer"]["tabs"][0]["tabRenderer"]["content"]["richGridRenderer"]
        second_grid = second["contents"]["twoColumnBrowseResultsRenderer"]["tabs"][0]["tabRenderer"]["content"]["richGridRenderer"]
        first_grid["targetId"], second_grid["targetId"] = "first-server-grid", "second-server-grid"
        feed, _ = feed_with(second, update([A], target="second-server-grid"), first=first)
        handle = next(value["id"] for value in feed.initialize()["filters"] if value["label"] == "Popular")
        feed.select_filter(handle)
        self.assertIn("second-server-grid", feed.targets)
        self.assertNotIn("first-server-grid", feed.targets)
        self.assertEqual([value["id"] for value in feed], [D, A])

    def test_close_has_no_more_requests_and_clears_tokens(self):
        feed, extractor = feed_with()
        feed.initialize()
        feed.close()
        self.assertEqual(feed.handles, {})
        self.assertIsNone(feed.config)
        self.assertEqual(list(feed), [])
        self.assertEqual(len(extractor.calls), 2)


if __name__ == "__main__":
    unittest.main()
