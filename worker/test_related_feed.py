"""Offline related-feed protocol/parser fixtures; no cookies or network are read."""

import copy
import io
import json
import unittest
from unittest.mock import patch

from yt_dlp.extractor.youtube import YoutubeIE, YoutubeTabIE

from related_feed import (MAX_DESCRIPTION, MAX_PAGES, RelatedFeedError, load_related,
                          parse_description, parse_rating, parse_recommendations, run, video_id_from_url)


A, B, C, D = "AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC", "DDDDDDDDDDD"
WATCH_QUERY = {"videoId": A, "contentCheckOk": True, "racyCheckOk": True}
CHANNEL = "UC" + "x" * 22
ACCOUNT = {"id": "fixture-channel", "pageId": "123", "dataSyncId": "123||user", "authUser": 1}


def video(video_id, *, members=False, byline="shortBylineText"):
    renderer = {"videoId": video_id, "title": {"simpleText": "Fixture " + video_id},
                "lengthText": {"simpleText": "2:03"},
                "viewCountText": {"simpleText": "1,234 views"},
                "thumbnail": {"thumbnails": [{"url": "https://i.ytimg.com/vi/" + video_id + "/hqdefault.jpg", "width": 480}]},
                byline: {"runs": [{"text": "Fixture channel", "navigationEndpoint": {
                    "browseEndpoint": {"browseId": CHANNEL, "canonicalBaseUrl": "/@fixture"}}}]}}
    if members:
        renderer["badges"] = [{"metadataBadgeRenderer": {"style": "BADGE_STYLE_TYPE_MEMBERS_ONLY"}}]
    return {"compactVideoRenderer": renderer}


def lockup(video_id, *, members=False, content_type="LOCKUP_CONTENT_TYPE_VIDEO"):
    metadata = {"metadataRows": [
        {"metadataParts": [{"text": {"content": "Modern channel", "commandRuns": [{"onTap": {
            "innertubeCommand": {"browseEndpoint": {"browseId": CHANNEL, "canonicalBaseUrl": "/@modern"}}}}]}}]},
        {"metadataParts": [{"text": {"content": "5K views"}}, {"text": {"content": "2 days ago"}, "accessibilityLabel": "2 days ago"}]},
    ]}
    if members:
        metadata["metadataRows"][0]["badges"] = [{"badgeViewModel": {"badgeStyle": "BADGE_MEMBERS_ONLY"}}]
    return {"lockupViewModel": {"contentId": video_id, "contentType": content_type,
        "metadata": {"lockupMetadataViewModel": {"title": {"content": "Modern fixture"},
            "metadata": {"contentMetadataViewModel": metadata}}},
        "contentImage": {"thumbnailViewModel": {"image": {"sources": [{
            "url": "https://i.ytimg.com/vi/" + video_id + "/hqdefault.jpg", "width": 480}]},
            "overlays": [{"thumbnailBottomOverlayViewModel": {"badges": [{
                "thumbnailBadgeViewModel": {"text": "4:56"}}]}}]}}}}


def continuation(token):
    return {"continuationItemRenderer": {"continuationEndpoint": {"continuationCommand": {"token": token}}}}


def initial(items, token=None):
    values = [*items, *([continuation(token)] if token else [])]
    return {"contents": {"twoColumnWatchNextResults": {"secondaryResults": {
        "secondaryResults": {"results": [{"itemSectionRenderer": {
            "targetId": "watch-next-feed", "contents": values}}]}}}}}


def continued(items, token=None, target="watch-next-feed"):
    return {"onResponseReceivedEndpoints": [{"appendContinuationItemsAction": {
        "targetId": target, "continuationItems": [*items, *([continuation(token)] if token else [])]}}]}


def rating_endpoint(status, video_id=A):
    path = {"LIKE": "like/like", "DISLIKE": "like/dislike", "INDIFFERENT": "like/removelike"}[status]
    return {"commandMetadata": {"webCommandMetadata": {"sendPost": True, "apiUrl": "/youtubei/v1/" + path}},
            "likeEndpoint": {"status": status, "target": {"videoId": video_id},
                             "likeParams": "synthetic-token-never-forwarded"}}


def old_toggle(status, value, *, target=A, disabled=False):
    return {"isToggled": value == {"LIKE": "like", "DISLIKE": "dislike"}[status], "isDisabled": disabled,
            "defaultIcon": {"iconType": status},
            "defaultServiceEndpoint": rating_endpoint(status, target),
            "toggledServiceEndpoint": rating_endpoint("INDIFFERENT", target)}


def old_rating(value="none", *, target=A, disabled=False):
    return {"segmentedLikeDislikeButtonRenderer": {
        "likeButton": {"toggleButtonRenderer": old_toggle("LIKE", value, target=target, disabled=disabled)},
        "dislikeButton": {"toggleButtonRenderer": old_toggle("DISLIKE", value, target=target, disabled=disabled)}}}


def modern_rating(value="none", *, target=A, disabled=False, doubled=True, entity=True):
    def toggle(status):
        def view(pending):
            return {"buttonViewModel": {"onTap": {"serialCommand": {"commands": [
                {"logGestureCommand": {"trackingParams": "synthetic-sensitive-log-token"}},
                {"innertubeCommand": rating_endpoint(pending, target)}]}}}}
        result = {"defaultButtonViewModel": view(status), "toggledButtonViewModel": view("INDIFFERENT"),
                  "isTogglingDisabled": disabled}
        return {"toggleButtonViewModel": result} if doubled else result
    key = "fixture-current-rating"
    like = {"toggleButtonViewModel": toggle("LIKE"), "likeStatusEntityKey": key}
    if entity:
        like["likeStatusEntity"] = {"key": key, "likeStatus": {
            "like": "LIKE", "dislike": "DISLIKE", "none": "INDIFFERENT", "unknown": "NEW_STATUS"}[value]}
    dislike = {"toggleButtonViewModel": toggle("DISLIKE"), "dislikeEntityKey": key}
    return {"segmentedLikeDislikeButtonViewModel": {
        "likeButtonViewModel": {"likeButtonViewModel": like} if doubled else like,
        "dislikeButtonViewModel": {"dislikeButtonViewModel": dislike} if doubled else dislike}}


def watch_rating(value="none", *, modern=True, logged_out=False, related=(), token=None, **kwargs):
    response = initial(related, token)
    response["contents"]["twoColumnWatchNextResults"]["results"] = {"results": {"contents": [
        {"videoPrimaryInfoRenderer": {"videoActions": {"menuRenderer": {"topLevelButtons": [
            modern_rating(value, **kwargs) if modern else old_rating(value, **kwargs)]}}}}]}}
    response["currentVideoEndpoint"] = {"watchEndpoint": {"videoId": A}}
    response["responseContext"] = {"mainAppWebResponseContext": {"loggedOut": logged_out}}
    return response


def rating_success():
    return {"responseContext": {"mainAppWebResponseContext": {"loggedOut": False}},
            "frameworkUpdates": {"entityBatchUpdate": {"mutations": []}}}


class FakeExtractor(YoutubeTabIE):
    """Use the actual pinned flat renderer parsers and fake only network boundaries."""
    def __init__(self, ydl, responses, *, authenticated=True, config=None):
        super().__init__(ydl)
        self.responses = list(responses)
        self.is_authenticated_fixture = authenticated
        self.config_fixture = copy.deepcopy(config or {"LOGGED_IN": True,
            "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}}})
        self.calls = []
        self.config_calls = 0

    @property
    def is_authenticated(self):
        return self.is_authenticated_fixture

    def initialize(self):
        pass

    def _download_ytcfg(self, client, video_id):
        self.config_calls += 1
        return self.config_fixture

    def generate_api_headers(self, **kwargs):
        self.header_arguments = copy.deepcopy(kwargs)
        return {"Authorization": "synthetic-only"}

    def _extract_context(self, config):
        return copy.deepcopy(config["INNERTUBE_CONTEXT"])

    def _call_api(self, endpoint, query, video_id, **kwargs):
        self.calls.append((endpoint, query, video_id, kwargs))
        if not self.responses:
            raise AssertionError("Unexpected network boundary call")
        value = self.responses.pop(0)
        if isinstance(value, BaseException):
            raise value
        return value


class FakeYdl:
    class Styles:
        EMPHASIS = ""

    def __init__(self, responses=(), **kwargs):
        self.params = {}
        self.extractor = FakeExtractor(self, responses, **kwargs)
        self.closed = False
        self.cookie_value_at_close = "not closed"
        self.options = None
        self.account = None

    def factory(self, options, account):
        self.options = options
        self.params.update(options)
        self.account = account
        return self

    def get_info_extractor(self, name):
        if name != "YoutubeTab":
            raise AssertionError("Only flat YouTubeTab parsing is allowed")
        return self.extractor

    def close(self):
        self.cookie_value_at_close = self.params.get("cookiefile")
        self.closed = True

    def report_warning(self, *_args, **_kwargs):
        pass

    def write_debug(self, *_args, **_kwargs):
        pass

    def _format_err(self, text, *_args, **_kwargs):
        return text


class RelatedFeedTests(unittest.TestCase):
    def request(self, **extra):
        return {"url": "https://www.youtube.com/watch?v=" + A, **extra}

    def test_watch_related_preserves_compact_metadata_and_membership(self):
        ydl = FakeYdl([initial([video(B, members=True, byline="longBylineText")])])
        result = load_related(self.request(), factory=ydl.factory)
        item = result["items"][0]
        self.assertEqual((item["id"], item["channel"], item["channelId"]), (B, "Fixture channel", CHANNEL))
        self.assertEqual((item["duration"], item["viewCount"], item["membersOnly"]), (123, 1234, True))
        self.assertEqual(result["source"], "youtube-next")
        self.assertEqual(result["sourceVideoId"], A)
        self.assertEqual(item["kind"], "video")
        self.assertEqual(ydl.extractor.calls[0][:3], ("next", WATCH_QUERY, A))
        self.assertFalse(ydl.options["mark_watched"])
        self.assertFalse(ydl.options["getcomments"])
        self.assertTrue(ydl.options["extract_flat"])
        self.assertTrue(ydl.closed)
        self.assertIsNone(ydl.cookie_value_at_close)

    def test_modern_lockup_membership_and_playlist_radios_filter(self):
        ydl = FakeYdl()
        response = initial([lockup(B, members=True), lockup("RDfixture", content_type="LOCKUP_CONTENT_TYPE_PLAYLIST"),
            {"compactRadioRenderer": {"playlistId": "RDfixture", "videoId": C}},
            {"compactPlaylistRenderer": {"playlistId": "PLfixture", "videoId": D}}])
        items, token, scoped = parse_recommendations(response, ydl.extractor)
        self.assertTrue(scoped)
        self.assertIsNone(token)
        self.assertEqual(len(items), 1)
        self.assertEqual((items[0]["channel"], items[0]["channelId"]), ("Modern channel", CHANNEL))
        self.assertEqual((items[0]["duration"], items[0]["viewCount"], items[0]["membersOnly"]), (296, 5000, True))

    def test_current_lockup_plain_channel_row_uses_matching_avatar_endpoint(self):
        card = lockup(B, members=True)
        metadata = card["lockupViewModel"]["metadata"]["lockupMetadataViewModel"]
        part = metadata["metadata"]["contentMetadataViewModel"]["metadataRows"][0]["metadataParts"][0]
        del part["text"]["commandRuns"]
        metadata["image"] = {"decoratedAvatarViewModel": {"rendererContext": {"commandContext": {
            "onTap": {"innertubeCommand": {"browseEndpoint": {"browseId": CHANNEL,
                "canonicalBaseUrl": "/@modern"}}}}}}}
        ydl = FakeYdl([initial([card])])
        item = load_related(self.request(), factory=ydl.factory)["items"][0]
        self.assertEqual((item["channel"], item["channelId"]), ("Modern channel", CHANNEL))
        self.assertTrue(item["membersOnly"])
        del metadata["image"]
        items, _, _ = parse_recommendations(initial([card]), ydl.extractor)
        self.assertEqual((items[0]["channel"], items[0]["channelId"]), ("", ""))

    def test_only_watch_secondary_surface_and_known_containers_are_parsed(self):
        response = initial([{"compactAutoplayRenderer": {"contents": [video(B)]}},
                            {"richItemRenderer": {"content": lockup(C)}},
                            {"adSlotRenderer": {"contents": video(D)}},
                            {"itemSectionRenderer": {"targetId": "comments-section", "contents": [video(D), continuation("comment-token")]}},
                            {"menuRenderer": {"contents": [video(D), continuation("menu-token")]}}])
        response["contents"]["twoColumnWatchNextResults"]["results"] = {"results": {"contents": [video(D), continuation("primary-token")]}}
        response["engagementPanels"] = [{"contents": [video(D), continuation("panel-token")]}]
        ydl = FakeYdl([response])
        result = load_related(self.request(), factory=ydl.factory)
        self.assertEqual([item["id"] for item in result["items"]], [B, C])
        self.assertEqual(len(ydl.extractor.calls), 1)

    def test_continuation_deduplicates_current_video_and_is_bounded(self):
        ydl = FakeYdl([initial([video(A), video(B), video(B)], "next-1"),
                       continued([video(B), video(C)], "next-2"),
                       continued([video(D)], "next-3")])
        result = load_related(self.request(), factory=ydl.factory)
        self.assertEqual([item["id"] for item in result["items"]], [B, C, D])
        self.assertEqual(len(ydl.extractor.calls), MAX_PAGES)
        self.assertEqual([call[1] for call in ydl.extractor.calls], [WATCH_QUERY, {"continuation": "next-1"}, {"continuation": "next-2"}])
        self.assertTrue(result["hasMore"])

    def test_repeated_related_token_and_comment_continuation_are_not_followed(self):
        ydl = FakeYdl([initial([video(B)], "loop"), continued([video(C)], "loop")])
        result = load_related(self.request(), factory=ydl.factory)
        self.assertEqual(len(ydl.extractor.calls), 2)
        self.assertFalse(result["hasMore"])
        ydl = FakeYdl([initial([video(B)], "related"), continued([video(D)], "comments", target="comments-section")])
        result = load_related(self.request(), factory=ydl.factory)
        self.assertEqual([item["id"] for item in result["items"]], [B])
        self.assertFalse(result["hasMore"])

    def test_limit_stops_without_extra_call_and_clamps_to_thirty(self):
        ydl = FakeYdl([initial([video(B), video(C)], "next")])
        result = load_related(self.request(maxItems=1), factory=ydl.factory)
        self.assertEqual(len(result["items"]), 1)
        self.assertEqual(len(ydl.extractor.calls), 1)
        self.assertTrue(result["hasMore"])
        ids = [f"{value:011d}" for value in range(40)]
        ydl = FakeYdl([initial([video(value) for value in ids])])
        self.assertEqual(len(load_related(self.request(maxItems=1000), factory=ydl.factory)["items"]), 30)

    def test_selected_channel_headers_and_context_match_without_cookie_save(self):
        config = {"LOGGED_IN": True, "DELEGATED_SESSION_ID": "999", "USER_SESSION_ID": "old",
                  "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}, "user": {"onBehalfOfUser": "999"}}}
        ydl = FakeYdl([initial([video(B)])], config=config)
        with patch("related_feed.Path.is_file", return_value=True):
            load_related(self.request(cookiePath="synthetic-lease.txt", accountContext=ACCOUNT), factory=ydl.factory)
        args = ydl.extractor.header_arguments
        self.assertEqual((args["delegated_session_id"], args["user_session_id"], args["session_index"]), ("123", "user", 1))
        self.assertEqual(ydl.extractor.calls[0][3]["context"]["user"]["onBehalfOfUser"], "123")
        self.assertEqual(config["DELEGATED_SESSION_ID"], "999")
        self.assertIsNone(ydl.cookie_value_at_close)

    def test_stale_auth_never_falls_back_to_guest_and_failure_closes_without_save(self):
        ydl = FakeYdl([initial([video(B)])], authenticated=False)
        with patch("related_feed.Path.is_file", return_value=True), self.assertRaises(RelatedFeedError) as raised:
            load_related(self.request(cookiePath="synthetic-only"), factory=ydl.factory)
        self.assertEqual(raised.exception.code, "auth")
        self.assertEqual(ydl.extractor.calls, [])
        self.assertEqual(ydl.extractor.config_calls, 0)
        self.assertTrue(ydl.closed)
        self.assertIsNone(ydl.cookie_value_at_close)
        response = initial([video(B)])
        response["responseContext"] = {"mainAppWebResponseContext": {"loggedOut": True}}
        ydl = FakeYdl([response])
        with patch("related_feed.Path.is_file", return_value=True), self.assertRaises(RelatedFeedError) as raised:
            load_related(self.request(cookiePath="synthetic-only"), factory=ydl.factory)
        self.assertEqual(raised.exception.code, "auth")
        self.assertIsNone(ydl.cookie_value_at_close)

    def test_supported_urls_are_normalized_and_arbitrary_hosts_rejected(self):
        for url in ("https://youtu.be/" + A + "?t=10", "youtube.com/watch?v=" + A + "&list=RDtest",
                    "//m.youtube.com/shorts/" + A, "http://www.youtube.com/live/" + A):
            self.assertEqual(video_id_from_url(url), A)
        for url in ("https://evil.test/watch?v=" + A, "https://youtube.com.evil.test/watch?v=" + A,
                    "https://user@www.youtube.com/watch?v=" + A, "https://www.youtube.com:444/watch?v=" + A,
                    "https://www.youtube.com/watch?v=" + A + "&v=" + B, "youtube.com/playlist?list=PLfixture",
                    "https://www.youtube.com/\\evil/watch?v=" + A, "https://youtu.be/short", None):
            with self.subTest(url=url), self.assertRaises(RelatedFeedError):
                video_id_from_url(url)

    def test_validation_before_factory_and_secret_diagnostics_are_suppressed(self):
        for request in (self.request(accountContext=ACCOUNT), self.request(maxItems=True),
                        self.request(maxItems=0), self.request(accountContext={"id": "bad"}), None):
            with patch("related_feed.create_youtube_dl") as factory, self.assertRaises(RelatedFeedError):
                load_related(request)
            factory.assert_not_called()
        ydl = FakeYdl([RuntimeError("synthetic secret cookie/path and upstream token")])
        output = io.StringIO()
        status = run([], stdin=io.StringIO(json.dumps(self.request()) + "\n"), stdout=output, factory=ydl.factory)
        self.assertEqual(status, 1)
        response = json.loads(output.getvalue())
        self.assertEqual((response["type"], response["code"]), ("error", "network"))
        self.assertNotIn("synthetic secret", output.getvalue())
        self.assertTrue(ydl.closed)

    def test_stdin_and_cli_each_emit_exactly_one_json_object(self):
        output = io.StringIO()
        ydl = FakeYdl([initial([video(B)])])
        self.assertEqual(run([], stdin=io.StringIO(json.dumps(self.request()) + "\n"), stdout=output, factory=ydl.factory), 0)
        self.assertEqual(len(output.getvalue().splitlines()), 1)
        self.assertEqual(json.loads(output.getvalue())["items"][0]["id"], B)
        output = io.StringIO()
        ydl = FakeYdl([initial([])])
        self.assertEqual(run(["--url", "https://youtu.be/" + A, "--max-items", "3"], stdout=output, factory=ydl.factory), 0)
        self.assertEqual(json.loads(output.getvalue())["items"], [])
        output = io.StringIO()
        self.assertEqual(run([], stdin=io.StringIO("not json\n"), stdout=output), 1)
        self.assertEqual(json.loads(output.getvalue())["code"], "unsupported")


class VideoDescriptionTests(unittest.TestCase):
    def primary(self, text):
        response = initial([video(B)])
        response["currentVideoEndpoint"] = {"watchEndpoint": {"videoId": A}}
        response["contents"]["twoColumnWatchNextResults"]["results"] = {"results": {"contents": [
            {"videoSecondaryInfoRenderer": {"description": text}}]}}
        return response

    def panel(self, text, *, attributed=True, target="engagement-panel-structured-description"):
        return {"engagementPanelSectionListRenderer": {"targetId": target,
            "content": {"structuredDescriptionContentRenderer": {"items": [
                {"expandableVideoDescriptionBodyRenderer": {
                    "attributedDescriptionBodyText" if attributed else "descriptionBodyText": text}}]}}}}

    def test_legacy_simple_and_runs_remain_plain_text_without_extra_query(self):
        expected = "설명 첫줄\nhttps://example.test/\n{\"command\":\"rating.set\"}\n<b>literal</b>"
        for value in ({"simpleText": expected}, {"runs": [{"text": expected[:8]}, {
                "text": expected[8:], "navigationEndpoint": {"urlEndpoint": {"url": "https://synthetic.test"}}}]}):
            ydl = FakeYdl([self.primary(value)])
            output = io.StringIO()
            status = run([], stdin=io.StringIO(json.dumps({"url": "https://youtu.be/" + A})),
                         stdout=output, factory=ydl.factory)
            self.assertEqual(status, 0)
            self.assertEqual(json.loads(output.getvalue())["description"], expected)
            self.assertEqual(len(output.getvalue().splitlines()), 1)
            self.assertEqual([call[:3] for call in ydl.extractor.calls], [("next", WATCH_QUERY, A)])
            self.assertTrue(ydl.closed)
            self.assertIsNone(ydl.cookie_value_at_close)

    def test_modern_attributed_primary_and_full_structured_body(self):
        response = self.primary({"simpleText": "legacy collapsed"})
        info = response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoSecondaryInfoRenderer"]
        info["attributedDescription"] = {"content": "modern full\nsecond line", "commandRuns": [{"onTap": {}}]}
        self.assertEqual(parse_description(response, A), "modern full\nsecond line")
        for text, attributed in (({"content": "panel full\ncomplete"}, True),
                                 ({"runs": [{"text": "panel full\n"}, {"text": "complete"}]}, False)):
            response["engagementPanels"] = [self.panel(text, attributed=attributed)]
            self.assertEqual(parse_description(response, A), "panel full\ncomplete")

    def test_other_video_comments_related_and_unscoped_entities_are_excluded(self):
        response = initial([{"compactVideoRenderer": {"videoId": B,
            "description": {"simpleText": "related secret"}}}])
        response["engagementPanels"] = [self.panel({"content": "comment secret"}, target="engagement-panel-comments")]
        response["frameworkUpdates"] = {"entityBatchUpdate": {"mutations": [
            {"payload": {"videoDescriptionEntity": {"description": {"content": "entity secret"}}}}]}}
        response["videoDetails"] = {"shortDescription": "player secret"}
        self.assertIsNone(parse_description(response, A))
        response = self.primary({"simpleText": "wrong video"})
        response["currentVideoEndpoint"]["watchEndpoint"]["videoId"] = B
        response["engagementPanels"] = [self.panel({"content": "wrong video panel"})]
        self.assertIsNone(parse_description(response, A))

    def test_description_is_first_page_only_and_continuation_stays_unchanged(self):
        first = self.primary({"simpleText": "first full description"})
        first["contents"]["twoColumnWatchNextResults"]["secondaryResults"]["secondaryResults"]["results"].append(continuation("next"))
        second = continued([video(C)])
        second["engagementPanels"] = [self.panel({"content": "later wrong description"})]
        ydl = FakeYdl([first, second])
        result = load_related({"url": "https://youtu.be/" + A}, factory=ydl.factory)
        self.assertEqual(result["description"], "first full description")
        self.assertEqual([call[1] for call in ydl.extractor.calls], [WATCH_QUERY, {"continuation": "next"}])

    def test_size_unicode_and_controls_are_bounded(self):
        description = parse_description(self.primary({"runs": [{"text": "가😀" * MAX_DESCRIPTION}]}), A)
        self.assertLessEqual(len(description.encode("utf-8")), MAX_DESCRIPTION)
        self.assertTrue(description.startswith("가😀"))
        self.assertNotIn("\ufffd", description)
        cleaned = parse_description(self.primary({"simpleText": "one\r\ntwo\rthree\tend\x00\x01\x7f\ud800"}), A)
        self.assertEqual(cleaned, "one\ntwo\nthree\tend")
        self.assertEqual(parse_description(self.primary({"runs": [{"text": "a" * 20000}, {"text": "b" * 20000}]}), A),
                         "a" * 20000 + "b" * (MAX_DESCRIPTION - 20000))

    def test_empty_missing_and_malformed_are_distinguished(self):
        self.assertEqual(parse_description(self.primary({"simpleText": ""}), A), "")
        for value in (None, 12, [], {"simpleText": {}}, {"runs": "bad"}, {"runs": [{"text": 12}]}):
            self.assertIsNone(parse_description(self.primary(value), A))
        response = {"contents": {"twoColumnWatchNextResults": {"results": {"results": {"contents": [
            {"videoSecondaryInfoRenderer": {"description": {"simpleText": "description only"}}}]}}}}}
        ydl = FakeYdl([response])
        self.assertEqual(load_related({"url": "https://youtu.be/" + A}, factory=ydl.factory)["description"], "description only")

    def test_pinned_player_and_next_already_acknowledge_simple_warning(self):
        extractor = YoutubeIE(FakeYdl())
        self.assertEqual(extractor._get_checkok_params(), {"contentCheckOk": True, "racyCheckOk": True})
        context = extractor._generate_player_context()
        self.assertIs(context["contentCheckOk"], True)
        self.assertIs(context["racyCheckOk"], True)
        with patch.object(extractor, "_extract_response", return_value=initial([])) as request:
            extractor._download_initial_data(A, None, "web", {})
        self.assertEqual(request.call_args.kwargs["query"], WATCH_QUERY)
        self.assertEqual(request.call_args.kwargs["ep"], "next")


class VideoRatingTests(unittest.TestCase):
    def request(self, command="rating.get", **extra):
        return {"command": command, "url": "https://www.youtube.com/watch?v=" + A, **extra}

    def authenticated(self, ydl, request):
        request = {"cookiePath": "synthetic-lease-only.txt", "accountContext": ACCOUNT, **request}
        with patch("related_feed.Path.is_file", return_value=True):
            return load_related(request, factory=ydl.factory)

    def test_old_modern_single_and_double_wrappers_parse_all_states(self):
        for modern, doubled in ((False, None), (True, False), (True, True)):
            for value in ("like", "dislike", "none"):
                kwargs = {"doubled": doubled} if modern else {}
                with self.subTest(modern=modern, doubled=doubled, value=value):
                    ydl = FakeYdl([watch_rating(value, modern=modern, **kwargs)])
                    result = self.authenticated(ydl, self.request())
                    self.assertEqual(result, {"type": "rating", "sourceVideoId": A, "value": value, "canRate": True})
                    self.assertEqual([call[:3] for call in ydl.extractor.calls], [("next", WATCH_QUERY, A)])
                    self.assertTrue(ydl.closed)
                    self.assertIsNone(ydl.cookie_value_at_close)

    def test_standalone_legacy_toggles_and_direct_like_renderer(self):
        response = watch_rating()
        menu = response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoPrimaryInfoRenderer"]["videoActions"]["menuRenderer"]
        menu["topLevelButtons"] = [{"toggleButtonRenderer": old_toggle(status, "dislike")}
                                    for status in ("LIKE", "DISLIKE")]
        self.assertEqual(parse_rating(response, A, authenticated=True), {"value": "dislike", "canRate": True})
        menu["topLevelButtons"] = [{"likeButtonRenderer": {"target": {"videoId": A}, "likeStatus": "LIKE", "likesAllowed": True}}]
        self.assertEqual(parse_rating(response, A, authenticated=True), {"value": "like", "canRate": True})

    def test_modern_status_mutations_are_bound_to_toolbar_entity_key(self):
        response = watch_rating(entity=False)
        response["frameworkUpdates"] = {"entityBatchUpdate": {"mutations": [
            {"payload": {"likeStatusEntity": {"key": "unrelated-comment", "likeStatus": "LIKE"}}},
            {"payload": {"likeStatusEntity": {"key": "fixture-current-rating", "likeStatus": "DISLIKE"}}}]}}
        self.assertEqual(parse_rating(response, A, authenticated=True), {"value": "dislike", "canRate": True})
        response["frameworkUpdates"]["entityBatchUpdate"]["mutations"].pop()
        self.assertEqual(parse_rating(response, A, authenticated=True), {"value": "unknown", "canRate": False})

    def test_related_returns_first_page_rating_without_an_extra_request(self):
        ydl = FakeYdl([watch_rating("like", related=[video(B)], token="related-more"),
                       continued([video(C)])])
        result = self.authenticated(ydl, self.request(command="related"))
        self.assertEqual(result["rating"], {"value": "like", "canRate": True})
        self.assertEqual([item["id"] for item in result["items"]], [B, C])
        self.assertEqual(len(ydl.extractor.calls), 2)

    def test_rating_get_never_parses_or_follows_recommendations(self):
        ydl = FakeYdl([watch_rating("none", related=[video(B)], token="never-followed")])
        with patch("related_feed.parse_recommendations", side_effect=AssertionError("Rating-only request parsed recommendations")):
            result = self.authenticated(ydl, self.request())
        self.assertEqual(result["value"], "none")
        self.assertEqual(len(ydl.extractor.calls), 1)
        self.assertNotIn("items", result)

    def test_guest_and_incognito_get_never_enable_rating_or_use_cookies(self):
        for request in (self.request(), self.request(incognito=True, cookiePath="synthetic-secret-do-not-read",
                                                     accountContext=ACCOUNT)):
            ydl = FakeYdl([watch_rating("none", logged_out=True)], authenticated=False)
            with patch("related_feed.Path.is_file", side_effect=AssertionError("Guest inspected a cookie path")):
                result = load_related(request, factory=ydl.factory)
            self.assertEqual((result["value"], result["canRate"]), ("none", False))
            self.assertNotIn("cookiefile", ydl.options)
            self.assertIsNone(ydl.account)
            self.assertEqual(len(ydl.extractor.calls), 1)

    def test_unknown_disabled_wrong_video_and_conflicting_states_fail_closed(self):
        cases = [initial([video(B)]), watch_rating("unknown"), watch_rating(disabled=True),
                 watch_rating(target=B), watch_rating(modern=False, target=B)]
        mismatch = watch_rating("like")
        mismatch["currentVideoEndpoint"]["watchEndpoint"]["videoId"] = B
        cases.append(mismatch)
        conflict = watch_rating(modern=False)
        old = conflict["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoPrimaryInfoRenderer"]["videoActions"]["menuRenderer"]["topLevelButtons"][0]["segmentedLikeDislikeButtonRenderer"]
        old["likeButton"]["toggleButtonRenderer"]["isToggled"] = True
        old["dislikeButton"]["toggleButtonRenderer"]["isToggled"] = True
        cases.append(conflict)
        for response in cases:
            with self.subTest(response=response):
                result = parse_rating(response, A, authenticated=True)
                self.assertFalse(result["canRate"])
        self.assertEqual(parse_rating(cases[0], A, authenticated=True)["value"], "unknown")
        self.assertEqual(parse_rating(cases[3], A, authenticated=True)["value"], "unknown")

    def test_unrelated_comment_and_recommendation_rating_cannot_be_current_state(self):
        response = initial([{"compactVideoRenderer": {"videoId": B, "segmentedLikeDislikeButtonViewModel": modern_rating("like")}}])
        response["engagementPanels"] = [watch_rating("dislike")]
        response["frameworkUpdates"] = {"entityBatchUpdate": {"mutations": [
            {"payload": {"likeStatusEntity": {"key": "fixture-current-rating", "likeStatus": "LIKE"}}}]}}
        self.assertEqual(parse_rating(response, A, authenticated=True), {"value": "unknown", "canRate": False})

    def test_arbitrary_renderer_endpoint_never_enables_writes(self):
        response = watch_rating(modern=False)
        toggle = response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoPrimaryInfoRenderer"]["videoActions"]["menuRenderer"]["topLevelButtons"][0]["segmentedLikeDislikeButtonRenderer"]["likeButton"]["toggleButtonRenderer"]
        toggle["defaultServiceEndpoint"]["commandMetadata"]["webCommandMetadata"]["apiUrl"] = "/youtubei/v1/arbitrary-write"
        ydl = FakeYdl([response])
        with self.assertRaises(RelatedFeedError) as raised:
            self.authenticated(ydl, self.request("rating.set", rating="like"))
        self.assertEqual(raised.exception.code, "rating")
        self.assertEqual([call[0] for call in ydl.extractor.calls], ["next"])

    def test_malformed_flags_ambiguous_models_and_conflicting_entity_keys_fail_closed(self):
        responses = []
        for flag in ("isDisabled", "isTogglingDisabled", "isToggled"):
            response = watch_rating(modern=False)
            button = response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoPrimaryInfoRenderer"]["videoActions"]["menuRenderer"]["topLevelButtons"][0]["segmentedLikeDislikeButtonRenderer"]["likeButton"]["toggleButtonRenderer"]
            button[flag] = "false"
            responses.append(response)
        response = watch_rating()
        wrapper = response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoPrimaryInfoRenderer"]["videoActions"]["menuRenderer"]["topLevelButtons"][0]
        wrapper.update(old_rating())
        responses.append(response)
        response = watch_rating()
        like_model = response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoPrimaryInfoRenderer"]["videoActions"]["menuRenderer"]["topLevelButtons"][0]["segmentedLikeDislikeButtonViewModel"]["likeButtonViewModel"]["likeButtonViewModel"]
        like_model["likeStatusEntity"]["key"] = "other-video-rating"
        responses.append(response)
        for response in responses:
            self.assertEqual(parse_rating(response, A, authenticated=True), {"value": "unknown", "canRate": False})

    def test_modern_nested_disabled_flags_fail_closed_before_rating_write(self):
        for doubled in (False, True):
            for side in ("like", "dislike"):
                for view_key in ("defaultButtonViewModel", "toggledButtonViewModel"):
                    for disabled in (True, False, "true", "false", 0, 1, None, [], {}):
                        with self.subTest(doubled=doubled, side=side, view=view_key, flag=disabled):
                            response = watch_rating(doubled=doubled)
                            modern = response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoPrimaryInfoRenderer"]["videoActions"]["menuRenderer"]["topLevelButtons"][0]["segmentedLikeDislikeButtonViewModel"]
                            model = modern[side + "ButtonViewModel"]
                            if doubled:
                                model = model[side + "ButtonViewModel"]
                            toggle = model["toggleButtonViewModel"]
                            if doubled:
                                toggle = toggle["toggleButtonViewModel"]
                            toggle[view_key]["buttonViewModel"]["isDisabled"] = disabled
                            expected = ({"value": "none", "canRate": disabled is False}
                                        if isinstance(disabled, bool) else {"value": "unknown", "canRate": False})
                            self.assertEqual(parse_rating(response, A, authenticated=True), expected)
                            if disabled is False:
                                continue
                            ydl = FakeYdl([response])
                            with self.assertRaises(RelatedFeedError):
                                self.authenticated(ydl, self.request("rating.set", rating="like"))
                            self.assertEqual([call[0] for call in ydl.extractor.calls], ["next"])

    def test_each_rating_uses_fixed_endpoint_target_only_and_verified_readback(self):
        for rating, endpoint in (("like", "like/like"), ("dislike", "like/dislike"), ("none", "like/removelike")):
            with self.subTest(rating=rating):
                ydl = FakeYdl([watch_rating(), rating_success(), watch_rating(rating)])
                result = self.authenticated(ydl, self.request("rating.set", rating=rating,
                    endpoint="https://synthetic.evil/arbitrary", params="synthetic-sensitive-input",
                    target={"videoId": B}, url="https://youtu.be/" + A + "?list=synthetic&t=50"))
                self.assertEqual(result, {"type": "rating", "sourceVideoId": A, "value": rating, "canRate": True, "verified": True})
                self.assertEqual([call[:3] for call in ydl.extractor.calls], [
                    ("next", WATCH_QUERY, A), (endpoint, {"target": {"videoId": A}}, A), ("next", WATCH_QUERY, A)])
                self.assertTrue(ydl.closed)
                self.assertIsNone(ydl.cookie_value_at_close)

    def test_selected_brand_channel_and_nonzero_authuser_apply_to_all_three_calls(self):
        config = {"LOGGED_IN": True, "DELEGATED_SESSION_ID": "999", "USER_SESSION_ID": "old",
                  "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}, "user": {"onBehalfOfUser": "999"}}}
        ydl = FakeYdl([watch_rating(), rating_success(), watch_rating("like")], config=config)
        result = self.authenticated(ydl, self.request("rating.set", rating="like"))
        self.assertTrue(result["verified"])
        args = ydl.extractor.header_arguments
        self.assertEqual((args["delegated_session_id"], args["user_session_id"], args["session_index"]), ("123", "user", 1))
        self.assertEqual(ydl.account, ACCOUNT)
        for call in ydl.extractor.calls:
            self.assertEqual(call[3]["context"]["user"]["onBehalfOfUser"], "123")
            self.assertEqual(call[3]["headers"], {"Authorization": "synthetic-only"})
        self.assertEqual(config["DELEGATED_SESSION_ID"], "999")

    def test_primary_channel_removes_previous_brand_context(self):
        primary = {"id": "primary:111", "pageId": "", "dataSyncId": "111||", "authUser": 2}
        config = {"LOGGED_IN": True, "DELEGATED_SESSION_ID": "999",
                  "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}, "user": {"onBehalfOfUser": "999"}}}
        ydl = FakeYdl([watch_rating(), rating_success(), watch_rating("dislike")], config=config)
        self.authenticated(ydl, self.request("rating.set", rating="dislike", accountContext=primary))
        args = ydl.extractor.header_arguments
        self.assertEqual((args["delegated_session_id"], args["user_session_id"], args["session_index"]), (None, "111", 2))
        for call in ydl.extractor.calls:
            self.assertNotIn("onBehalfOfUser", call[3]["context"]["user"])

    def test_bad_rating_command_id_and_incognito_are_rejected_before_factory(self):
        invalid = [self.request("rating.set", rating=value) for value in (None, "LIKE", "unknown", "arbitrary", True, [], {})]
        invalid.extend([self.request("like/like", rating="like"), self.request("rating.set", rating="like", url="https://youtu.be/bad-id"),
                        self.request("rating.set", rating="like", url="https://evil.test/watch?v=" + A),
                        self.request("rating.set", rating="like", incognito=True, cookiePath="synthetic-secret"),
                        self.request(incognito="false")])
        for request in invalid:
            with self.subTest(request=request), patch("related_feed.create_youtube_dl") as factory, self.assertRaises(RelatedFeedError):
                load_related(request)
            factory.assert_not_called()

    def test_guest_set_and_invalid_cookie_or_account_cannot_start_network_work(self):
        invalid = [self.request("rating.set", rating="like"),
                   self.request("rating.set", rating="like", cookiePath="missing-synthetic-cookie"),
                   self.request("rating.set", rating="like", cookiePath="synthetic-cookie", accountContext={"id": "bad"})]
        for request in invalid:
            with patch("related_feed.Path.is_file", return_value=False), patch("related_feed.create_youtube_dl") as factory, self.assertRaises(RelatedFeedError):
                load_related(request)
            factory.assert_not_called()

    def test_authentication_config_and_authorization_fail_before_any_api_call(self):
        for kwargs in ({"authenticated": False}, {"config": {"LOGGED_IN": False, "INNERTUBE_CONTEXT": {}}},
                       {"config": {"INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}}}}):
            ydl = FakeYdl([watch_rating()], **kwargs)
            with self.assertRaises(RelatedFeedError) as raised:
                self.authenticated(ydl, self.request("rating.set", rating="like"))
            self.assertEqual(raised.exception.code, "auth")
            self.assertEqual(ydl.extractor.calls, [])
            self.assertTrue(ydl.closed)
            self.assertIsNone(ydl.cookie_value_at_close)
        ydl = FakeYdl([watch_rating()])
        with patch.object(ydl.extractor, "generate_api_headers", return_value={}):
            with self.assertRaises(RelatedFeedError) as raised:
                self.authenticated(ydl, self.request("rating.set", rating="like"))
        self.assertEqual(raised.exception.code, "auth")
        self.assertEqual(ydl.extractor.calls, [])

    def test_loggedout_and_missing_auth_evidence_stop_preflight_before_write(self):
        missing = watch_rating()
        del missing["responseContext"]
        for response in (watch_rating(logged_out=True), missing):
            ydl = FakeYdl([response])
            with self.assertRaises(RelatedFeedError) as raised:
                self.authenticated(ydl, self.request("rating.set", rating="like"))
            self.assertEqual(raised.exception.code, "auth")
            self.assertEqual([call[0] for call in ydl.extractor.calls], ["next"])
            self.assertTrue(ydl.closed)

    def test_unavailable_or_disabled_preflight_never_posts(self):
        for response in (initial([video(B)]), watch_rating("unknown"), watch_rating(disabled=True),
                         watch_rating(target=B)):
            ydl = FakeYdl([response])
            with self.assertRaises(RelatedFeedError):
                self.authenticated(ydl, self.request("rating.set", rating="like"))
            self.assertEqual([call[0] for call in ydl.extractor.calls], ["next"])

    def test_write_error_or_malformed_response_is_not_a_success_or_retried(self):
        for write_response in ([], "synthetic-secret-response", {}, {"error": {}}, {"error": {"message": "synthetic-secret"}},
                               {"success": False, "responseContext": {}}, {"responseContext": "synthetic-secret"},
                               {"responseContext": {"mainAppWebResponseContext": {"loggedOut": True}}}):
            ydl = FakeYdl([watch_rating(), write_response])
            with self.assertRaises(RelatedFeedError):
                self.authenticated(ydl, self.request("rating.set", rating="like"))
            self.assertEqual([call[0] for call in ydl.extractor.calls], ["next", "like/like"])
            self.assertTrue(ydl.closed)
            self.assertIsNone(ydl.cookie_value_at_close)

    def test_verify_mismatch_unknown_error_and_loggedout_are_explicitly_unverified(self):
        for verify in (watch_rating("none"), watch_rating("unknown"), initial([video(B)]),
                       {"error": {"message": "synthetic-secret-verify-error"}}, watch_rating("like", logged_out=True),
                       RuntimeError("synthetic-secret-verify-cookie-and-token")):
            ydl = FakeYdl([watch_rating(), rating_success(), verify])
            result = self.authenticated(ydl, self.request("rating.set", rating="like"))
            self.assertFalse(result["verified"])
            self.assertNotEqual(result["value"], "like")
            self.assertIn("확인", result["message"])
            self.assertNotIn("synthetic-secret", json.dumps(result))
            self.assertEqual([call[0] for call in ydl.extractor.calls], ["next", "like/like", "next"])

    def test_secret_input_and_noisy_upstream_diagnostics_do_not_leak_in_stdout(self):
        ydl = FakeYdl([watch_rating()])
        original = ydl.extractor._call_api
        def noisy(*args, **kwargs):
            print("synthetic-sensitive-cookie-path-and-token")
            if args[0] != "next":
                raise RuntimeError("synthetic-sensitive-cookie-path-and-token")
            return original(*args, **kwargs)
        ydl.extractor._call_api = noisy
        request = self.request("rating.set", rating="like", cookiePath="synthetic-sensitive-cookie-path",
                               accountContext=ACCOUNT, arbitrary="synthetic-sensitive-cookie-value")
        output, leaked = io.StringIO(), io.StringIO()
        with patch("related_feed.Path.is_file", return_value=True), patch("sys.stdout", leaked):
            status = run([], stdin=io.StringIO(json.dumps(request) + "\n"), stdout=output, factory=ydl.factory)
        self.assertEqual(status, 1)
        self.assertEqual(json.loads(output.getvalue())["type"], "error")
        self.assertEqual(len(output.getvalue().splitlines()), 1)
        self.assertNotIn("synthetic-sensitive", output.getvalue())
        self.assertEqual(leaked.getvalue(), "")
        self.assertTrue(ydl.closed)

    def test_interrupt_closes_without_cookie_save_and_never_retries(self):
        for responses in ([KeyboardInterrupt()], [watch_rating(), KeyboardInterrupt()],
                          [watch_rating(), rating_success(), KeyboardInterrupt()]):
            ydl = FakeYdl(responses)
            with self.assertRaises(KeyboardInterrupt):
                self.authenticated(ydl, self.request("rating.set", rating="like"))
            self.assertTrue(ydl.closed)
            self.assertIsNone(ydl.cookie_value_at_close)
            self.assertLessEqual(len([call for call in ydl.extractor.calls if call[0] == "like/like"]), 1)

    def test_protocol_get_and_set_emit_one_typed_json_object(self):
        for request, responses in ((self.request(), [watch_rating("dislike")]),
                                    (self.request("rating.set", rating="like"), [watch_rating(), rating_success(), watch_rating("like")])):
            ydl = FakeYdl(responses)
            request.update(cookiePath="synthetic-lease-only", accountContext=ACCOUNT)
            output = io.StringIO()
            with patch("related_feed.Path.is_file", return_value=True):
                status = run([], stdin=io.StringIO(json.dumps(request) + "\n"), stdout=output, factory=ydl.factory)
            self.assertEqual(status, 0)
            self.assertEqual(len(output.getvalue().splitlines()), 1)
            result = json.loads(output.getvalue())
            self.assertEqual((result["type"], result["sourceVideoId"], result["canRate"]), ("rating", A, True))


if __name__ == "__main__":
    unittest.main()
