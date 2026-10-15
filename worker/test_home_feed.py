"""Offline home/chip fixtures using the bundled flat renderer parsers."""

import copy
import json
import unittest

from yt_dlp.extractor.youtube import YoutubeTabIE

from home_feed import HOME_ID, HOME_TARGET, HomeFeed, HomeFeedError, _browse_command
from recommendation_actions import extract_feedback_actions


A, B, C, D = "AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC", "DDDDDDDDDDD"
CHANNEL = "UC" + "x" * 22
ACCOUNT = {"id": "fixture-channel", "pageId": "123", "dataSyncId": "123||user", "authUser": 1}


def endpoint(token, *, request="CONTINUATION_REQUEST_TYPE_BROWSE"):
    return {"commandMetadata": {"webCommandMetadata": {"apiUrl": "/youtubei/v1/browse", "sendPost": True}},
            "clickTrackingParams": "tracking-" + token,
            "continuationCommand": {"token": token, "request": request}}


def chip(label, token=None, *, selected=False, modern=False):
    if modern:
        value = {"text": label, "selected": selected}
        if token is not None:
            value["tapCommand"] = {"innertubeCommand": endpoint(token)}
        return {"chipViewModel": value}
    value = {"text": {"simpleText": label}, "isSelected": selected}
    if token is not None:
        value["navigationEndpoint"] = endpoint(token)
    return {"chipCloudChipRenderer": value}


def chips(*values, modern=False):
    return {"chipBarViewModel" if modern else "feedFilterChipBarRenderer": {
        "chips" if modern else "contents": list(values)}}


def video(video_id, *, menu=None, members=False):
    renderer = {"videoId": video_id, "title": {"simpleText": "Fixture " + video_id},
                "lengthText": {"simpleText": "2:03"}, "viewCountText": {"simpleText": "1,234 views"},
                "thumbnail": {"thumbnails": [{"url": f"https://i.ytimg.com/vi/{video_id}/hqdefault.jpg", "width": 480}]},
                "shortBylineText": {"runs": [{"text": "Fixture channel", "navigationEndpoint": {
                    "browseEndpoint": {"browseId": CHANNEL, "canonicalBaseUrl": "/@fixture"}}}]}}
    if menu is not None:
        renderer["menu"] = menu
    if members:
        renderer["badges"] = [{"metadataBadgeRenderer": {"style": "BADGE_STYLE_TYPE_MEMBERS_ONLY"}}]
    return {"richItemRenderer": {"content": {"videoRenderer": renderer}}}


def lockup(content_id, *, kind="VIDEO", seed=None, menu=None):
    metadata = {"title": {"content": "Modern fixture"}, "metadata": {"contentMetadataViewModel": {
        "metadataRows": [{"metadataParts": [{"text": {"content": "Modern channel", "commandRuns": [
            {"onTap": {"innertubeCommand": {"browseEndpoint": {"browseId": CHANNEL}}}}]}}]}]}}}
    if menu is not None:
        metadata["menuButton"] = menu
    renderer = {"contentId": content_id, "contentType": "LOCKUP_CONTENT_TYPE_" + kind,
                "metadata": {"lockupMetadataViewModel": metadata}}
    if kind == "VIDEO":
        renderer["contentImage"] = {"thumbnailViewModel": {"image": {"sources": [{
            "url": f"https://i.ytimg.com/vi/{content_id}/hqdefault.jpg", "width": 480}]}}}
    else:
        renderer["contentImage"] = {"collectionThumbnailViewModel": {"primaryThumbnail": {
            "thumbnailViewModel": {"image": {"sources": [{"url": "https://i.ytimg.com/fixture.jpg", "width": 480}]}}}}}
    if seed:
        renderer["rendererContext"] = {"commandContext": {"onTap": {"innertubeCommand": {
            "watchEndpoint": {"videoId": seed, "playlistId": content_id}}}}}
    return {"richItemRenderer": {"content": {"lockupViewModel": renderer}}}


def cursor(token):
    return {"continuationItemRenderer": {"continuationEndpoint": endpoint(token)}}


def initial(items=(), token=None, filters=None):
    renderer = {"contents": [*items, *([cursor(token)] if token else [])]}
    if filters is not None:
        renderer["header"] = filters
    return {"contents": {"twoColumnBrowseResultsRenderer": {"tabs": [{"tabRenderer": {
        "selected": True, "tabIdentifier": HOME_ID, "title": "Home", "content": {"richGridRenderer": renderer}}}]}}}


def continued(items=(), token=None, filters=None, *, target=HOME_TARGET, reload=False):
    result = {"onResponseReceivedActions": [{"reloadContinuationItemsCommand" if reload else "appendContinuationItemsAction": {
        "targetId": target, "continuationItems": [*items, *([cursor(token)] if token else [])]}}]}
    if filters is not None:
        result["onResponseReceivedActions"].append({"reloadContinuationItemsCommand": {
            "targetId": "feed-filter-chip-bar", "continuationItems": [filters]}})
    return result


class FakeExtractor(YoutubeTabIE):
    def __init__(self, ydl, responses=(), *, authenticated=True, config=None):
        super().__init__(ydl)
        self.responses = list(responses)
        self.authenticated_fixture = authenticated
        self.config_fixture = copy.deepcopy(config if config is not None else {
            "LOGGED_IN": True, "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}}})
        self.calls, self.header_calls = [], []
        self.config_calls = self.initialize_calls = 0

    @property
    def is_authenticated(self):
        return self.authenticated_fixture

    def initialize(self):
        self.initialize_calls += 1

    def _download_ytcfg(self, client, item):
        self.config_calls += 1
        self.assert_client = client
        return self.config_fixture

    def generate_api_headers(self, **kwargs):
        self.header_calls.append(copy.deepcopy(kwargs))
        return {"Authorization": "fixture-only"}

    def _extract_context(self, config):
        return copy.deepcopy(config["INNERTUBE_CONTEXT"])

    def _call_api(self, endpoint, query, item, **kwargs):
        self.calls.append((endpoint, copy.deepcopy(query), item, copy.deepcopy(kwargs)))
        if not self.responses:
            raise AssertionError("Unexpected API boundary call")
        value = self.responses.pop(0)
        if isinstance(value, Exception):
            raise value
        return value


class FakeYdl:
    class Styles:
        EMPHASIS = ""

    def __init__(self):
        self.params = {"mark_watched": False, "extract_flat": True}

    def report_warning(self, *_args, **_kwargs):
        pass

    def write_debug(self, *_args, **_kwargs):
        pass

    def _format_err(self, text, *_args, **_kwargs):
        return text

    def extract_info(self, *_args, **_kwargs):
        raise AssertionError("No individual or playlist extraction")


def make_feed(*responses, account=None, **kwargs):
    ydl = FakeYdl()
    extractor = FakeExtractor(ydl, responses, **kwargs)
    feed = HomeFeed(ydl, account, extractor_factory=lambda _: extractor)
    return feed, extractor


class HomeFeedTests(unittest.TestCase):
    def test_bootstrap_is_lazy_and_metadata_available_without_consuming_first_card(self):
        feed, extractor = make_feed(initial([video(A, members=True)], "next", chips(
            chip("All", selected=True), chip("New to you", "new"), chip("Music", "music"))))
        self.assertEqual(extractor.calls, [])
        result = feed.initialize()
        self.assertEqual(result["filterKind"], "home")
        self.assertEqual([value["label"] for value in result["filters"]], ["All", "New to you", "Music"])
        self.assertTrue(result["filters"][0]["selected"])
        feed.initialize()
        item = next(feed)
        self.assertEqual((item["id"], item["channel"], item["duration"], item["view_count"]),
                         (A, "Fixture channel", 123, 1234))
        self.assertEqual(item["availability"], "subscriber_only")
        self.assertEqual(extractor.calls[0][:3], ("browse", {"browseId": HOME_ID}, "home"))
        self.assertEqual((extractor.config_calls, extractor.initialize_calls, len(extractor.calls)), (1, 1, 1))
        self.assertNotIn("music", json.dumps(result))

    def test_selected_chip_sends_server_query_and_rotates_handles_and_resets_cursor(self):
        feed, extractor = make_feed(initial([video(A), video(D)], "old", chips(
            chip("All", selected=True), chip("Music", "music-filter"))),
            continued([video(B)], "music-next", chips(chip("All", "all"), chip("Music", selected=True)), reload=True),
            continued([video(C)]))
        feed.initialize()
        handle = feed.metadata["filters"][1]["id"]
        old_handles = {value["id"] for value in feed.metadata["filters"]}
        feed.select_filter(handle)
        self.assertEqual(extractor.calls[1][1], {"continuation": "music-filter"})
        self.assertEqual(extractor.calls[1][3]["context"]["clickTracking"], {"clickTrackingParams": "tracking-music-filter"})
        self.assertTrue(old_handles.isdisjoint(value["id"] for value in feed.metadata["filters"]))
        with self.assertRaises(HomeFeedError) as raised:
            feed.select_filter(handle)
        self.assertEqual(raised.exception.code, "unsupported")
        self.assertEqual([value["id"] for value in feed], [B, C])
        self.assertEqual(extractor.calls[2][1], {"continuation": "music-next"})
        self.assertEqual(len(extractor.calls), 3)

    def test_current_selected_bootstrap_chip_and_return_to_all_reuse_actual_feed_request(self):
        filters = chips(chip("All", selected=True), chip("Mixes", "mix-filter"))
        feed, extractor = make_feed(initial([video(A)], filters=filters), initial([video(B)], filters=filters),
                                    continued([lockup("RDfixture", kind="PLAYLIST", seed=C)], reload=True),
                                    initial([video(D)], filters=filters))
        feed.initialize()
        feed.select_filter(feed.metadata["filters"][0]["id"])
        self.assertEqual(extractor.calls[1][1], {"browseId": HOME_ID})
        feed.select_filter(feed.metadata["filters"][1]["id"])
        self.assertEqual([value["label"] for value in feed.metadata["filters"]], ["All", "Mixes"])
        self.assertTrue(feed.metadata["filters"][1]["selected"])
        self.assertEqual(next(feed)["url"], "https://www.youtube.com/watch?v=CCCCCCCCCCC&list=RDfixture")
        feed.select_filter(feed.metadata["filters"][0]["id"])
        self.assertEqual(extractor.calls[3][1], {"browseId": HOME_ID})
        self.assertEqual(next(feed)["id"], D)

    def test_modern_chips_and_browse_endpoint_are_supported_without_label_guessing(self):
        browse = {"chipViewModel": {"text": "서버의 임의 주제", "selected": False,
            "tapCommand": {"innertubeCommand": {"browseEndpoint": {"browseId": HOME_ID, "params": "actual-server-params"}}}}}
        filters = chips(chip("전체", selected=True, modern=True), browse, modern=True)
        feed, extractor = make_feed(initial([video(A)], filters=filters), continued([video(B)], reload=True))
        feed.initialize()
        feed.select_filter(feed.metadata["filters"][1]["id"])
        self.assertEqual(extractor.calls[1][1], {"browseId": HOME_ID, "params": "actual-server-params"})
        self.assertEqual(next(feed)["id"], B)
        self.assertEqual(feed.metadata["filters"][1]["label"], "서버의 임의 주제")

    def test_real_web_chip_loading_presentation_is_validated_and_stripped_from_request(self):
        # Same structural shape as the live WEB chip, with synthetic tokens.
        command = endpoint("s" * 604)
        command["clickTrackingParams"] = "t" * 48
        command["continuationCommand"].update({"invalidationGroupId": HOME_TARGET,
            "command": {"clickTrackingParams": "u" * 48, "showReloadUiCommand": {
                "targetId": HOME_TARGET, "content": {"ghostGridViewModel": {"numRows": 4}}}}})
        value = chip("Server music")
        value["chipCloudChipRenderer"]["navigationEndpoint"] = command
        feed, extractor = make_feed(initial([video(A)], filters=chips(chip("All", selected=True), value)),
                                    continued([video(B)], reload=True))
        feed.initialize()
        self.assertEqual([value["label"] for value in feed.metadata["filters"]], ["All", "Server music"])
        feed.select_filter(feed.metadata["filters"][1]["id"])
        self.assertEqual(extractor.calls[1][1], {"continuation": "s" * 604})
        self.assertNotIn("showReloadUiCommand", json.dumps(extractor.calls))
        self.assertNotIn("invalidationGroupId", json.dumps(extractor.calls))
        self.assertNotIn("ghostGridViewModel", json.dumps(extractor.calls))
        self.assertEqual(next(feed)["id"], B)

    def test_loading_placeholder_cannot_smuggle_other_commands_or_surfaces(self):
        base = endpoint("synthetic-filter")
        base["continuationCommand"].update({"invalidationGroupId": HOME_TARGET,
            "command": {"showReloadUiCommand": {"targetId": HOME_TARGET,
                "content": {"ghostGridViewModel": {"numRows": 4}}}}})
        invalid = []
        value = copy.deepcopy(base)
        value["continuationCommand"]["command"]["feedbackEndpoint"] = {"feedbackToken": "never-write"}
        invalid.append(value)
        value = copy.deepcopy(base)
        value["continuationCommand"]["command"]["showReloadUiCommand"]["targetId"] = "comments-section"
        invalid.append(value)
        value = copy.deepcopy(base)
        value["continuationCommand"]["command"]["showReloadUiCommand"]["content"] = {"playerResponse": {}}
        invalid.append(value)
        value = copy.deepcopy(base)
        value["continuationCommand"]["command"]["showReloadUiCommand"]["content"]["ghostGridViewModel"]["numRows"] = True
        invalid.append(value)
        value = copy.deepcopy(base)
        value["continuationCommand"]["invalidationGroupId"] = "x" * 257
        invalid.append(value)
        for value in invalid:
            with self.subTest(value=value):
                self.assertIsNone(_browse_command(value))

    def test_no_chips_does_not_manufacture_filters(self):
        feed, _ = make_feed(initial([video(A)]))
        feed.initialize()
        self.assertEqual(feed.metadata["filters"], [])
        with self.assertRaises(HomeFeedError):
            feed.select_filter("music")

    def test_unknown_or_malformed_chip_endpoints_are_not_exposed(self):
        malicious = [chip("Watch", "watch"), chip("Feedback", "feedback"), chip("Other browse", "other"),
                     chip("Wrong request", "wrong"), chip("Command bundle", "bundle")]
        malicious[0]["chipCloudChipRenderer"]["navigationEndpoint"] = {"watchEndpoint": {"videoId": A}}
        malicious[1]["chipCloudChipRenderer"]["navigationEndpoint"] = {"feedbackEndpoint": {"feedbackToken": "secret"}}
        malicious[2]["chipCloudChipRenderer"]["navigationEndpoint"] = {"browseEndpoint": {"browseId": "FEhistory"}}
        malicious[3]["chipCloudChipRenderer"]["navigationEndpoint"] = endpoint("wrong", request="CONTINUATION_REQUEST_TYPE_WATCH_NEXT")
        malicious[4]["chipCloudChipRenderer"]["navigationEndpoint"] = {"commandExecutorCommand": {"commands": [endpoint("bundle")]}}
        feed, extractor = make_feed(initial([video(A)], filters=chips(chip("All", selected=True), *malicious)))
        feed.initialize()
        self.assertEqual([value["label"] for value in feed.metadata["filters"]], ["All"])
        self.assertEqual(len(extractor.calls), 1)
        for value in (None, {"continuationCommand": {"token": "a\nsecret"}},
                      {"continuationCommand": {"token": "a" * 16385}},
                      {"browseEndpoint": {"browseId": HOME_ID, "params": "a"}, "watchEndpoint": {"videoId": A}},
                      {**endpoint("test"), "commandMetadata": {"webCommandMetadata": {"apiUrl": "/youtubei/v1/player"}}}):
            self.assertIsNone(_browse_command(value))

    def test_selected_brand_channel_headers_and_body_match_through_filter_and_continuation(self):
        config = {"LOGGED_IN": True, "DELEGATED_SESSION_ID": "999", "USER_SESSION_ID": "old",
                  "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}, "user": {"onBehalfOfUser": "999"}}}
        feed, extractor = make_feed(initial([video(A)], filters=chips(chip("All", selected=True), chip("Music", "music"))),
                                    continued([video(B)], "next"), continued([video(C)]), account=ACCOUNT, config=config)
        feed.initialize()
        feed.select_filter(feed.metadata["filters"][1]["id"])
        self.assertEqual([value["id"] for value in feed], [B, C])
        arguments = extractor.header_calls[0]
        self.assertEqual((arguments["delegated_session_id"], arguments["user_session_id"], arguments["session_index"]), ("123", "user", 1))
        for _ep, _query, _item, kwargs in extractor.calls:
            self.assertEqual(kwargs["context"]["user"]["onBehalfOfUser"], "123")
            self.assertEqual(kwargs["headers"]["Authorization"], "fixture-only")
        self.assertEqual(config["DELEGATED_SESSION_ID"], "999")
        self.assertEqual(feed.config["DELEGATED_SESSION_ID"], "123")

    def test_duplicate_cards_and_repeated_continuation_token_stop(self):
        feed, extractor = make_feed(initial([video(A), video(A)], "one"),
                                    continued([video(A), video(B)], "two"), continued([video(B), video(C)], "one"))
        self.assertEqual([value["id"] for value in feed], [A, B, C])
        self.assertEqual(len(extractor.calls), 3)

    def test_oversize_home_page_is_error_not_eof(self):
        feed, _ = make_feed(initial([video(f"{i:011d}") for i in range(257)], token="after-overflow"))
        with self.assertRaises(HomeFeedError):
            feed.initialize()

    def test_home_traversal_budget_is_error_not_eof(self):
        from home_feed import _parse_cards
        extractor = FakeExtractor(FakeYdl())
        with self.assertRaises(HomeFeedError):
            _parse_cards([{"contents": [None] * 4097}], extractor)

    def test_home_depth_budget_is_error_not_eof(self):
        from home_feed import _parse_cards
        contents = video(A)
        for _ in range(18):
            contents = {"itemSectionRenderer": {"contents": [contents]}}
        with self.assertRaises(HomeFeedError):
            _parse_cards([{"contents": [contents]}], FakeExtractor(FakeYdl()))

    def test_full_bounded_page_preserves_following_cursor(self):
        ids = [f"{value:011d}" for value in range(256)]
        feed, extractor = make_feed(initial([video(value) for value in ids], "next"), continued([video(A)]))
        self.assertEqual([value["id"] for value in feed], [*ids, A])
        self.assertEqual(len(extractor.calls), 2)

    def test_continuation_chip_bar_does_not_revoke_or_retarget_visible_handles(self):
        feed, extractor = make_feed(initial([video(A)], "next", chips(
            chip("All", selected=True), chip("Music", "original-music"))),
            continued([video(B)], filters=chips(chip("All", "updated-all", selected=True),
                chip("Music", "updated-music"), chip("Mixes", "new-mix"))),
            continued([video(C)], reload=True))
        feed.initialize()
        metadata = copy.deepcopy(feed.metadata)
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(next(feed)["id"], B)
        self.assertEqual(feed.metadata, metadata)
        feed.select_filter(metadata["filters"][1]["id"])
        self.assertEqual(extractor.calls[2][1], {"continuation": "original-music"})
        self.assertEqual(next(feed)["id"], C)

    def test_empty_pages_and_total_pages_are_bounded(self):
        feed, extractor = make_feed(initial([video(A)], "one"), continued([], "two"),
                                    continued([video(A)], "three"), continued([], "four"))
        self.assertEqual(next(feed)["id"], A)
        with self.assertRaises(HomeFeedError):
            next(feed)
        self.assertEqual(len(extractor.calls), 4)
        feed, extractor = make_feed(initial([video(A)], "one"), continued([video(B)], "two"))
        feed.max_pages = 1
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(next(feed)["id"], B)
        with self.assertRaises(HomeFeedError):
            next(feed)
        self.assertEqual(len(extractor.calls), 2)

    def test_scoped_surfaces_exclude_ad_menu_other_tab_and_comment_continuations(self):
        response = initial([video(A), {"adSlotRenderer": {"content": video(D)}},
                            {"menuRenderer": {"contents": [video(D), cursor("menu")]}},
                            {"itemSectionRenderer": {"targetId": "comments-section", "contents": [video(D), cursor("comments")]}}])
        response["contents"]["twoColumnBrowseResultsRenderer"]["tabs"].append({"tabRenderer": {
            "selected": False, "content": {"richGridRenderer": {"contents": [video(D), cursor("other-tab")]}}}})
        response["engagementPanels"] = [{"contents": [video(D), cursor("panel")]}]
        feed, extractor = make_feed(response)
        self.assertEqual([value["id"] for value in feed], [A])
        self.assertEqual(len(extractor.calls), 1)
        feed, extractor = make_feed(initial([video(A)], "one"), continued([video(D)], "comment", target="comments-section"))
        self.assertEqual(next(feed)["id"], A)
        with self.assertRaises(HomeFeedError):
            next(feed)
        self.assertEqual(len(extractor.calls), 2)

    def test_video_playlist_radio_and_modern_mix_cards_stay_flat_and_preserve_feedback_renderer(self):
        menu = {"menuRenderer": {"items": [{"menuServiceItemRenderer": {"text": {"simpleText": "Not interested"},
            "serviceEndpoint": {"feedbackEndpoint": {"feedbackToken": "inert-fixture-token"}}}}]}}
        playlist = {"richItemRenderer": {"content": {"playlistRenderer": {"playlistId": "PLfixture",
            "title": {"simpleText": "Playlist fixture"}, "thumbnail": {"thumbnails": [{"url": "https://i.ytimg.com/fixture.jpg"}]}}}}}
        radio = {"richItemRenderer": {"content": {"radioRenderer": {"playlistId": "RDlegacy", "videoId": C,
            "title": {"simpleText": "Mix fixture"}, "thumbnail": {"thumbnails": []}}}}}
        feed, extractor = make_feed(initial([video(A, menu=menu), lockup(B, menu=menu), playlist, radio,
                                    lockup("RDmodern", kind="PLAYLIST", seed=D, menu=menu), lockup("PLpodcast", kind="PODCAST")]))
        entries = list(feed)
        self.assertEqual([value["id"] for value in entries], [A, B, "PLfixture", "RDlegacy", "RDmodern", "PLpodcast"])
        self.assertEqual(entries[3]["url"], "https://www.youtube.com/watch?v=CCCCCCCCCCC&list=RDlegacy")
        self.assertEqual(entries[4]["url"], "https://www.youtube.com/watch?v=DDDDDDDDDDD&list=RDmodern")
        self.assertEqual(entries[0]["_recommendation_renderer"]["menu"], menu)
        self.assertEqual(entries[1]["_recommendation_renderer"]["metadata"]["lockupMetadataViewModel"]["menuButton"], menu)
        self.assertEqual(extract_feedback_actions(entries[0]["_recommendation_renderer"])["not_interested"]["feedbackToken"], "inert-fixture-token")
        self.assertEqual([value["ie_key"] for value in entries], ["Youtube", "Youtube", "YoutubeTab", "YoutubeTab", "YoutubeTab", "YoutubeTab"])
        self.assertEqual(len(extractor.calls), 1)

    def test_failed_filter_does_not_discard_cards_or_current_handles(self):
        feed, extractor = make_feed(initial([video(A)], filters=chips(chip("All", selected=True), chip("Music", "music"))),
                                    {"error": {"code": 500}})
        feed.initialize()
        original = copy.deepcopy(feed.metadata)
        with self.assertRaises(HomeFeedError):
            feed.select_filter(feed.metadata["filters"][1]["id"])
        self.assertEqual(feed.metadata, original)
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(len(extractor.calls), 2)

    def test_auth_is_checked_before_browse_and_logged_out_response_never_becomes_guest_feed(self):
        feed, extractor = make_feed(initial([video(A)]), authenticated=False)
        with self.assertRaises(HomeFeedError) as raised:
            feed.initialize()
        self.assertEqual(raised.exception.code, "auth")
        self.assertEqual((extractor.calls, extractor.config_calls), ([], 0))
        feed, extractor = make_feed(initial([video(A)]), config={"LOGGED_IN": False})
        with self.assertRaises(HomeFeedError) as raised:
            feed.initialize()
        self.assertEqual(raised.exception.code, "auth")
        self.assertEqual(extractor.calls, [])
        response = initial([video(A)])
        response["responseContext"] = {"mainAppWebResponseContext": {"loggedOut": True}}
        feed, _ = make_feed(response)
        with self.assertRaises(HomeFeedError) as raised:
            feed.initialize()
        self.assertEqual(raised.exception.code, "auth")

    def test_empty_home_is_valid_but_nonfeed_payload_and_invalid_cards_are_rejected(self):
        feed, _ = make_feed(initial([]))
        self.assertEqual(list(feed), [])
        feed, _ = make_feed({"responseContext": {"unrelated": True}})
        with self.assertRaises(HomeFeedError):
            feed.initialize()
        feed, _ = make_feed(initial([video("invalid"), lockup("invalid"),
                                    lockup("PLvalid", kind="PRODUCT"), {"playlistRenderer": {"playlistId": "bad&list=other"}}]))
        self.assertEqual(list(feed), [])

    def test_close_erases_handles_and_does_not_make_requests(self):
        feed, extractor = make_feed(initial([video(A)], "next", chips(chip("All", selected=True))))
        feed.initialize()
        handle = feed.metadata["filters"][0]["id"]
        feed.close()
        self.assertIsNone(feed.config)
        self.assertEqual(feed.metadata["filters"], [])
        self.assertEqual(list(feed), [])
        with self.assertRaises(HomeFeedError):
            feed.select_filter(handle)
        self.assertEqual(len(extractor.calls), 1)


if __name__ == "__main__":
    unittest.main()
