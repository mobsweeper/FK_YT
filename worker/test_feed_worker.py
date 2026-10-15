"""Offline fixtures only; no browser, account, yt-dlp package, or network needed."""

import contextlib
import http.cookiejar
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from export_session import export_session, permitted_domain
from feed_worker import FeedWorker, browse_url, normalize_entry, run
from recommendation_actions import (RecommendationError, RecommendationSession,
                                    extract_feedback_actions)


_ORIGINAL_TEMPDIR = tempfile.tempdir


def setUpModule():
    # Keep fixture writes inside the authorized workspace, including nested copies.
    test_root = Path(__file__).resolve().parents[1] / "artifacts" / "worker-tests"
    test_root.mkdir(parents=True, exist_ok=True)
    tempfile.tempdir = str(test_root)


def tearDownModule():
    tempfile.tempdir = _ORIGINAL_TEMPDIR


def video(index):
    return {"_type": "url", "ie_key": "Youtube", "id": f"v{index:010d}", "title": f"Video {index}"}


def request(command="feed.open", *, source="home", generation=1, request_id="r1", **fields):
    return {"command": command, "requestId": request_id, "generation": generation,
            "source": source, "batchSize": 90, "cookiePath": None, **fields}


class LazyFixture:
    def __init__(self, entries):
        self.source = iter(entries)
        self.produced = 0
        self.closed = False

    def __iter__(self):
        return self

    def __next__(self):
        entry = next(self.source)
        self.produced += 1
        return entry

    def close(self):
        self.closed = True


class FakeYDL:
    def __init__(self, options, resolver):
        self.options = options
        self.resolver = resolver
        self.calls = []
        self.closed = False

    def extract_info(self, url, *, download, process):
        self.calls.append((url, download, process))
        return self.resolver(url)

    def close(self):
        self.closed = True
        if self.options.get("cookiefile"):
            Path(self.options["cookiefile"]).write_text("updated managed cookies", encoding="utf-8")


class Factory:
    def __init__(self, resolver):
        self.resolver = resolver
        self.instances = []

    def __call__(self, options):
        instance = FakeYDL(options, self.resolver)
        self.instances.append(instance)
        return instance


def items(events):
    return [event["item"] for event in events if event["type"] == "item"]


def feedback_menu_item(action="not_interested", token="fixture-token", *, icon=None, label=None):
    return {"menuServiceItemRenderer": {
        "icon": {"iconType": icon or ("NOT_INTERESTED" if action == "not_interested" else "REMOVE")},
        "text": {"simpleText": label or ("Not interested" if action == "not_interested" else "Don't recommend channel")},
        "serviceEndpoint": {
            "clickTrackingParams": "fixture-tracking",
            "commandMetadata": {"webCommandMetadata": {"sendPost": True, "apiUrl": "/youtubei/v1/feedback"}},
            "feedbackEndpoint": {"feedbackToken": token, "uiActions": {"hideEnclosingContainer": True}}}}}


def feedback_video(index, *, channel_id="UC_fixture"):
    renderer = {"menu": {"menuRenderer": {"items": [
        feedback_menu_item(token=f"private-video-{index}"),
        feedback_menu_item("dont_recommend_channel", f"private-channel-{index}")]}}}
    return {**video(index), "channel_id": channel_id,
            "_recommendation_endpoints": extract_feedback_actions(renderer)}


class FeedWorkerTests(unittest.TestCase):
    def make_worker(self, entries, **kwargs):
        cursor = LazyFixture(entries)
        factory = Factory(lambda _url: {"_type": "playlist", "entries": cursor})
        worker = FeedWorker(factory, **kwargs)
        self.addCleanup(worker.close)
        return worker, factory, cursor

    def test_270_items_append_across_three_90_item_batches(self):
        worker, factory, cursor = self.make_worker(video(i) for i in range(270))
        first = list(worker.events(request()))
        self.assertEqual(len(items(first)), 90)
        self.assertEqual(cursor.produced, 91)  # Exactly one lookahead, never the entire feed.
        self.assertTrue(first[-1]["hasMore"])
        second = list(worker.events(request("feed.next", request_id="r2")))
        self.assertEqual(cursor.produced, 181)
        third = list(worker.events(request("feed.next", request_id="r3")))
        combined = items(first) + items(second) + items(third)
        self.assertEqual([item["id"] for item in combined], [video(i)["id"] for i in range(270)])
        self.assertEqual([batch[-1]["count"] for batch in (first, second, third)], [90, 90, 90])
        self.assertFalse(third[-1]["hasMore"])
        self.assertEqual(len(factory.instances), 1)
        self.assertEqual(factory.instances[0].calls, [("https://www.youtube.com/feed/recommended", False, False)])

    def test_feed_duplicates_are_removed_across_batch_boundary(self):
        for source in ("home", "subscriptions", "search:sample query"):
            with self.subTest(source=source):
                worker, factory, _cursor = self.make_worker(video(i // 2) for i in range(270))
                first = list(worker.events(request(source=source)))
                second = list(worker.events(request("feed.next", source=source)))
                ids = [item["id"] for item in items(first) + items(second)]
                self.assertEqual(ids, [video(i)["id"] for i in range(135)])
                self.assertEqual(len(factory.instances[0].calls), 1)
                self.assertFalse(second[-1]["hasMore"])

    def test_real_playlist_preserves_duplicate_order(self):
        expected = [video(i % 5)["id"] for i in range(150)]
        worker, _factory, _cursor = self.make_worker(video(i % 5) for i in range(150))
        source = "https://www.youtube.com/playlist?list=PL_fixture"
        first = list(worker.events(request(source=source)))
        second = list(worker.events(request("feed.next", source=source)))
        self.assertEqual([item["id"] for item in items(first) + items(second)], expected)
        self.assertFalse(second[-1]["hasMore"])

    def test_members_only_metadata_uses_extractor_availability(self):
        self.assertTrue(normalize_entry({**video(1), "availability": "subscriber_only"})["membersOnly"])
        for availability in (None, "public", "unlisted", "private", "premium_only", "needs_auth", ""):
            with self.subTest(availability=availability):
                result = normalize_entry({**video(1), "availability": availability,
                                          "title": "Members only membership subscriber exclusive"})
                self.assertFalse(result["membersOnly"])
        playlist = normalize_entry({"_type": "url", "ie_key": "YoutubeTab", "id": "PL_fixture",
                                    "url": "https://www.youtube.com/playlist?list=PL_fixture",
                                    "availability": "subscriber_only"})
        self.assertFalse(playlist["membersOnly"])

    def test_members_only_are_filtered_before_batch_count_and_seen(self):
        entries = [{**video(i), "availability": "subscriber_only" if i % 2 == 0 else "public"}
                   for i in range(270)]
        # A hidden occurrence must not prevent the later public occurrence of
        # the same ID from being delivered by a deduplicating feed.
        entries.insert(0, {**video(1), "availability": "subscriber_only"})
        worker, factory, cursor = self.make_worker(entries)
        first = list(worker.events(request()))
        second = list(worker.events(request("feed.next", request_id="r2", hideMembersOnly=False)))
        self.assertEqual([item["id"] for item in items(first) + items(second)],
                         [video(i)["id"] for i in range(1, 270, 2)])
        self.assertEqual([batch[-1]["count"] for batch in (first, second)], [90, 45])
        self.assertTrue(first[-1]["hasMore"])
        self.assertFalse(second[-1]["hasMore"])
        self.assertEqual(sum(batch[-1]["skippedMembersOnly"] for batch in (first, second)), 136)
        self.assertEqual(cursor.produced, len(entries))
        self.assertEqual(factory.instances[0].calls,
                         [("https://www.youtube.com/feed/recommended", False, False)])

    def test_members_only_filter_can_be_disabled_for_entire_cursor(self):
        worker, factory, _cursor = self.make_worker(
            {**video(i), "availability": "subscriber_only"} for i in range(95))
        first = list(worker.events(request(hideMembersOnly=False)))
        second = list(worker.events(request("feed.next", request_id="r2", hideMembersOnly=True)))
        self.assertEqual([item["id"] for item in items(first) + items(second)],
                         [video(i)["id"] for i in range(95)])
        self.assertTrue(all(item["membersOnly"] for item in items(first) + items(second)))
        self.assertNotIn("skippedMembersOnly", first[-1])
        self.assertNotIn("skippedMembersOnly", second[-1])
        self.assertEqual(len(factory.instances[0].calls), 1)

    def test_members_only_filter_preserves_real_playlist_duplicate_order(self):
        entries = [{**video(1), "availability": "subscriber_only"}, video(2), video(2), video(3)]
        worker, _factory, _cursor = self.make_worker(entries)
        events = list(worker.events(request(source="https://www.youtube.com/playlist?list=PL_fixture")))
        self.assertEqual([item["id"] for item in items(events)], [video(i)["id"] for i in (2, 2, 3)])
        self.assertEqual(events[-1]["skippedMembersOnly"], 1)

    def test_members_only_empty_result_and_scan_budget_are_bounded(self):
        worker, _factory, _cursor = self.make_worker(
            {**video(i), "availability": "subscriber_only"} for i in range(10))
        events = list(worker.events(request()))
        self.assertEqual(items(events), [])
        self.assertEqual(events[-1]["type"], "done")
        self.assertEqual(events[-1]["count"], 0)
        self.assertFalse(events[-1]["hasMore"])
        self.assertEqual(events[-1]["skippedMembersOnly"], 10)

        def endless():
            while True:
                yield {**video(1), "availability": "subscriber_only"}
        worker, _factory, cursor = self.make_worker(endless(), scan_factor=1)
        events = list(worker.events(request()))
        self.assertEqual(events[-1]["code"], "unavailable")
        self.assertLessEqual(cursor.produced, 91)

    def test_members_only_scan_failure_keeps_delivered_items(self):
        def generator():
            yield video(1)
            while True:
                yield {**video(2), "availability": "subscriber_only"}
        worker, factory, cursor = self.make_worker(generator(), scan_factor=1)
        events = list(worker.events(request()))
        self.assertEqual([item["id"] for item in items(events)], [video(1)["id"]])
        self.assertEqual(events[-1]["type"], "done")
        self.assertEqual(events[-1]["warning"]["code"], "unavailable")
        self.assertEqual(events[-1]["skippedMembersOnly"], 90)
        self.assertFalse(events[-1]["hasMore"])
        self.assertTrue(cursor.closed)
        self.assertTrue(factory.instances[0].closed)

    def test_members_only_filter_flag_requires_boolean_before_extraction(self):
        for value in (None, 0, 1, "true", "false", [], {}):
            with self.subTest(value=value):
                worker, factory, _cursor = self.make_worker([video(1)])
                events = list(worker.events(request(hideMembersOnly=value)))
                self.assertEqual(events[-1]["code"], "unsupported")
                self.assertEqual(factory.instances, [])

    def test_recommendation_handles_hide_tokens_and_submit_exact_action_once(self):
        writes = []
        def sender(endpoint, video_id, account_context):
            writes.append((endpoint, video_id, account_context))
            print("private write diagnostics")
            return True
        worker, factory, cursor = self.make_worker([feedback_video(1), feedback_video(2)], feedback_sender=sender)
        first = list(worker.events(request()))
        self.assertNotIn("private-", json.dumps(first))
        item = items(first)[0]
        self.assertEqual(item["channelId"], "UC_fixture")
        action = request("recommendation.action", action="not_interested", videoId=item["id"],
                         actionHandle=item["recommendationActions"]["not_interested"])
        stdout, stderr = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            applied = list(worker.events(action))
        self.assertEqual(applied, [{"type": "feedback.result", "requestId": "r1", "generation": 1,
                                   "success": True, "action": "not_interested", "videoId": video(1)["id"],
                                   "channelId": "UC_fixture"}])
        self.assertEqual(writes[0][0]["feedbackToken"], "private-video-1")
        self.assertEqual(writes[0][1:], (video(1)["id"], None))
        self.assertEqual(stdout.getvalue() + stderr.getvalue(), "")
        self.assertEqual(list(worker.events(action))[-1]["code"], "unavailable")
        self.assertEqual(len(writes), 1)
        self.assertFalse(cursor.closed)
        self.assertFalse(factory.instances[0].closed)
        self.assertEqual(len(factory.instances[0].calls), 1)

    def test_recommendation_failures_do_not_close_cursor_or_allow_retrying_consumed_handle(self):
        writes = []
        def sender(*args):
            writes.append(args)
            raise TimeoutError("private-token-auth-diagnostics")
        worker, factory, cursor = self.make_worker([feedback_video(i) for i in range(95)], feedback_sender=sender)
        first = list(worker.events(request()))
        item = items(first)[0]
        action = request("recommendation.action", action="not_interested", videoId=item["id"],
                         actionHandle=item["recommendationActions"]["not_interested"])
        failed = list(worker.events(action))
        self.assertEqual(failed[-1]["code"], "network")
        self.assertNotIn("private-token", json.dumps(failed))
        self.assertEqual(list(worker.events(action))[-1]["code"], "unavailable")
        self.assertEqual(len(writes), 1)
        self.assertEqual(len(items(list(worker.events(request("feed.next"))))), 5)
        self.assertFalse(cursor.closed)
        self.assertFalse(factory.instances[0].closed)

    def test_recommendation_requires_exact_cursor_account_video_action_and_handle(self):
        account = {"id": "fixture-brand", "pageId": "123", "dataSyncId": "123||fixture-user", "authUser": 0}
        other_account = {"id": "fixture-primary", "pageId": "", "dataSyncId": "fixture-user||", "authUser": 0}
        writes = []
        worker, _factory, _cursor = self.make_worker([feedback_video(1)], feedback_sender=lambda *args: writes.append(args) or True)
        item = items(list(worker.events(request(accountContext=account))))[0]
        fields = {"action": "not_interested", "videoId": item["id"],
                  "actionHandle": item["recommendationActions"]["not_interested"], "accountContext": account}
        invalid = ({"generation": 2}, {"accountContext": other_account}, {"accountContext": None},
                   {"action": "dont_recommend_channel"}, {"videoId": video(2)["id"]},
                   {"actionHandle": "invented-handle"}, {"action": "like"}, {"action": {}},
                   {"videoId": "invalid"}, {"actionHandle": []})
        for override in invalid:
            with self.subTest(override=override):
                event = list(worker.events(request("recommendation.action", **{**fields, **override})))[-1]
                self.assertEqual(event["type"], "error")
        self.assertEqual(writes, [])
        self.assertTrue(list(worker.events(request("recommendation.action", **fields)))[-1]["success"])
        self.assertEqual(writes[0][2], account)

    def test_recommendation_old_handles_are_invalid_after_close_or_reopen(self):
        writes = []
        worker, _factory, _cursor = self.make_worker([feedback_video(1)], feedback_sender=lambda *args: writes.append(args) or True)
        item = items(list(worker.events(request())))[0]
        action = request("recommendation.action", action="not_interested", videoId=item["id"],
                         actionHandle=item["recommendationActions"]["not_interested"])
        list(worker.events(request("feed.close")))
        self.assertEqual(list(worker.events(action))[-1]["code"], "unavailable")
        list(worker.events(request(generation=2)))
        self.assertEqual(list(worker.events({**action, "generation": 2}))[-1]["code"], "unavailable")
        self.assertEqual(writes, [])

    def test_channel_recommendation_success_suppresses_queued_channel_and_other_handles(self):
        entries = [feedback_video(1), feedback_video(2), feedback_video(3, channel_id="UC_other")]
        writes = []
        worker, _factory, _cursor = self.make_worker(entries, feedback_sender=lambda *args: writes.append(args) or True)
        item = items(list(worker.events(request(batchSize=1))))[0]
        action = request("recommendation.action", action="dont_recommend_channel", videoId=item["id"],
                         actionHandle=item["recommendationActions"]["dont_recommend_channel"])
        self.assertTrue(list(worker.events(action))[-1]["success"])
        self.assertEqual(writes[0][0]["feedbackToken"], "private-channel-1")
        remaining = items(list(worker.events(request("feed.next"))))
        self.assertEqual([item["id"] for item in remaining], [video(3)["id"]])

    def test_recommendation_server_nonconfirmation_keeps_items(self):
        worker, _factory, _cursor = self.make_worker([feedback_video(1)], feedback_sender=lambda *args: False)
        item = items(list(worker.events(request())))[0]
        failed = list(worker.events(request("recommendation.action", action="not_interested", videoId=item["id"],
                                            actionHandle=item["recommendationActions"]["not_interested"])))
        self.assertEqual(failed[-1]["code"], "feedback")
        self.assertEqual(worker.dismissed_videos, set())
        self.assertEqual(worker.dismissed_channels, set())

    def test_short_recommendation_source_uses_own_lazy_cursor_without_home_extraction(self):
        worker, factory, _cursor = self.make_worker([])
        shorts = LazyFixture(video(i) for i in range(185))
        with patch("feed_worker.ShortsFeed", return_value=shorts) as adapter:
            first = list(worker.events(request(source="shorts")))
            second = list(worker.events(request("feed.next")))
            third = list(worker.events(request("feed.next")))
        self.assertEqual([event[-1]["count"] for event in (first, second, third)], [90, 90, 5])
        self.assertEqual(adapter.call_args.args, (factory.instances[0], None))
        self.assertEqual(factory.instances[0].calls, [])
        self.assertFalse(third[-1]["hasMore"])
        worker.close()
        self.assertTrue(shorts.closed)

    def test_raw_redirect_is_followed_without_processing_video_entries(self):
        cursor = LazyFixture(video(i) for i in range(10))
        target = "https://www.youtube.com/playlist?list=PL_fixture"
        factory = Factory(lambda url: {"_type": "url", "url": target}
                          if url.endswith("recommended") else {"_type": "playlist", "entries": cursor})
        worker = FeedWorker(factory)
        self.addCleanup(worker.close)
        events = list(worker.events(request()))
        self.assertEqual(len(items(events)), 10)
        self.assertEqual(factory.instances[0].calls, [
            ("https://www.youtube.com/feed/recommended", False, False), (target, False, False)])

    def test_redirect_to_individual_video_is_never_extracted(self):
        factory = Factory(lambda _url: {"_type": "url", "url": "https://www.youtube.com/watch?v=v0000000001"})
        worker = FeedWorker(factory)
        self.addCleanup(worker.close)
        events = list(worker.events(request()))
        self.assertEqual(events[-1]["code"], "unsupported")
        self.assertEqual(len(factory.instances[0].calls), 1)

    def test_direct_video_rejected_before_constructing_ytdlp(self):
        factory = Factory(lambda _url: None)
        worker = FeedWorker(factory)
        for source in ("https://www.youtube.com/watch?v=v0000000001", "https://youtu.be/v0000000001",
                       "https://www.youtube.com/shorts/v0000000001"):
            self.assertEqual(list(worker.events(request(source=source)))[-1]["code"], "unsupported")
        self.assertEqual(factory.instances, [])

    def test_bare_channel_opens_single_videos_tab(self):
        worker, factory, _cursor = self.make_worker([video(1)])
        events = list(worker.events(request(source="https://www.youtube.com/@fixture")))
        self.assertEqual(events[-1]["type"], "done")
        self.assertEqual(factory.instances[0].calls, [("https://www.youtube.com/@fixture/videos", False, False)])

    def test_only_supported_channel_tabs_and_exact_shapes_are_accepted(self):
        for channel in ("/@fixture", "/channel/UC_fixture", "/c/fixture", "/user/fixture"):
            for tab in ("videos", "shorts", "streams", "playlists"):
                with self.subTest(channel=channel, tab=tab):
                    url = "https://www.youtube.com" + channel + "/" + tab
                    self.assertEqual(browse_url(url), url)
        factory = Factory(lambda _url: None)
        worker = FeedWorker(factory)
        self.addCleanup(worker.close)
        for path in ("/@fixture/about", "/@fixture/community", "/@fixture/live",
                     "/@fixture/videos/unrelated", "/channel/UC_fixture/videos/unrelated",
                     "/c/fixture/search", "/user/fixture/about", "/channel//videos"):
            with self.subTest(path=path):
                events = list(worker.events(request(source="https://www.youtube.com" + path)))
                self.assertEqual(events[-1]["code"], "unsupported")
        self.assertEqual(factory.instances, [])

    def test_browse_watch_list_is_canonicalized_in_explicit_list_context(self):
        for source in ("https://www.youtube.com/watch?list=PL_fixture",
                       "http://youtube.com/watch?v=v0000000001&list=PL_fixture&t=12",
                       "youtube.com/watch?v=v0000000001&list=PL_fixture",
                       "www.youtube.com/playlist?list=PL_fixture"):
            with self.subTest(source=source):
                worker, factory, _cursor = self.make_worker([video(1)])
                events = list(worker.events(request(source=source)))
                self.assertEqual(events[-1]["type"], "done")
                self.assertEqual(factory.instances[0].calls,
                                 [("https://www.youtube.com/playlist?list=PL_fixture", False, False)])

    def test_video_seed_mix_url_opens_the_watch_playlist_panel(self):
        expected = "https://www.youtube.com/watch?v=5EQSpS5WvdI&list=RD5EQSpS5WvdI"
        for source in ("https://www.youtube.com/playlist?list=RD5EQSpS5WvdI",
                       "youtube.com/watch?list=RD5EQSpS5WvdI",
                       "http://m.youtube.com/watch?v=5EQSpS5WvdI&list=RD5EQSpS5WvdI&t=12"):
            with self.subTest(source=source):
                self.assertEqual(browse_url(source), expected)

    def test_mix_watch_keeps_explicit_seed_without_inventing_an_unknown_seed(self):
        for playlist_id in ("RDMM", "RDEMfixture", "RDAMVMv0000000001"):
            with self.subTest(playlist_id=playlist_id):
                self.assertEqual(
                    browse_url("https://www.youtube.com/watch?v=v0000000001&list=" + playlist_id),
                    "https://www.youtube.com/watch?v=v0000000001&list=" + playlist_id)
                self.assertEqual(
                    browse_url("https://www.youtube.com/playlist?list=" + playlist_id),
                    "https://www.youtube.com/playlist?list=" + playlist_id)
        self.assertEqual(browse_url("https://www.youtube.com/watch?v=invalid&list=RDMM"),
                         "https://www.youtube.com/playlist?list=RDMM")

    def test_home_mix_card_retains_seed_and_can_open_a_single_lazy_cursor(self):
        mix_url = "https://www.youtube.com/watch?v=5EQSpS5WvdI&list=RD5EQSpS5WvdI"
        card = normalize_entry({"_type": "url", "ie_key": "YoutubeTab", "id": "RD5EQSpS5WvdI",
                                "url": "https://www.youtube.com/playlist?list=RD5EQSpS5WvdI",
                                "title": "Mix - Darren Styles & Gammer - You & I"})
        self.assertEqual((card["kind"], card["id"], card["url"]),
                         ("playlist", "RD5EQSpS5WvdI", mix_url))
        expected = [video(i % 7)["id"] for i in range(190)]
        cursor = LazyFixture(video(i % 7) for i in range(190))
        def resolver(url):
            if url != mix_url:
                raise RuntimeError("fixture invalid Mix playlist page")
            return {"_type": "playlist", "entries": cursor}
        factory = Factory(resolver)
        worker = FeedWorker(factory)
        self.addCleanup(worker.close)
        first = list(worker.events(request(source=card["url"])))
        self.assertEqual(first[-1]["type"], "done")
        self.assertEqual(cursor.produced, 91)
        second = list(worker.events(request("feed.next", source=card["url"], request_id="r2")))
        third = list(worker.events(request("feed.next", source=card["url"], request_id="r3")))
        self.assertEqual([item["id"] for item in items(first) + items(second) + items(third)], expected)
        self.assertEqual([batch[-1]["count"] for batch in (first, second, third)], [90, 90, 10])
        self.assertFalse(third[-1]["hasMore"])
        self.assertEqual(factory.instances[0].calls, [(mix_url, False, False)])

    def test_explicit_mix_card_seed_wins_over_playlist_id_seed(self):
        for key in ("YoutubeTab", "YoutubePlaylist"):
            with self.subTest(key=key):
                card = normalize_entry({"_type": "url", "ie_key": key, "id": "RD5EQSpS5WvdI",
                                        "url": "https://www.youtube.com/watch?v=v0000000001&list=RD5EQSpS5WvdI"})
                self.assertEqual((card["kind"], card["url"]),
                                 ("playlist", "https://www.youtube.com/watch?v=v0000000001&list=RD5EQSpS5WvdI"))

    def test_mix_redirect_preserves_seed_and_never_extracts_individual_video(self):
        source = "https://www.youtube.com/feed/playlists"
        target = "https://www.youtube.com/watch?v=v0000000001&list=RDMM"
        factory = Factory(lambda url: {"_type": "url", "ie_key": "YoutubeTab", "url": target}
                          if url == source else {"_type": "playlist", "entries": [video(1)]})
        worker = FeedWorker(factory)
        self.addCleanup(worker.close)
        events = list(worker.events(request(source=source)))
        self.assertEqual(events[-1]["type"], "done")
        self.assertEqual(factory.instances[0].calls, [(source, False, False), (target, False, False)])

    def test_watch_list_entry_preserves_playlist_but_video_with_list_stays_video(self):
        playlist = normalize_entry({"_type": "url", "ie_key": "YoutubeTab", "id": "PL_fixture",
                                    "url": "https://www.youtube.com/watch?list=PL_fixture"})
        self.assertIsNotNone(playlist)
        self.assertEqual((playlist["kind"], playlist["id"], playlist["url"]),
                         ("playlist", "PL_fixture", "https://www.youtube.com/playlist?list=PL_fixture"))
        for entry in ({"_type": "url", "url": "https://www.youtube.com/watch?v=v0000000001&list=PL_fixture"},
                      {**video(1), "url": "https://www.youtube.com/watch?v=v0000000001&list=PL_fixture"}):
            with self.subTest(entry=entry):
                result = normalize_entry(entry)
                self.assertEqual((result["kind"], result["id"], result["url"]),
                                 ("video", "v0000000001", "https://www.youtube.com/watch?v=v0000000001"))

    def test_explicit_playlist_extractor_with_video_preview_opens_playlist(self):
        for key in ("YoutubeTab", "YoutubePlaylist"):
            with self.subTest(key=key):
                entry = {"_type": "url", "ie_key": key, "id": "PL_fixture",
                         "url": "https://www.youtube.com/watch?v=v0000000001&list=PL_fixture"}
                result = normalize_entry(entry)
                self.assertEqual((result["kind"], result["id"], result["url"]),
                                 ("playlist", "PL_fixture", "https://www.youtube.com/playlist?list=PL_fixture"))

    def test_redirect_loop_is_bounded(self):
        factory = Factory(lambda _url: {"_type": "url", "url": "https://www.youtube.com/feed/recommended"})
        worker = FeedWorker(factory)
        events = list(worker.events(request()))
        self.assertEqual(events[-1]["code"], "unsupported")
        self.assertLessEqual(len(factory.instances[0].calls), 3)

    def test_empty_and_exhausted_cursor(self):
        worker, _factory, _cursor = self.make_worker([])
        events = list(worker.events(request()))
        self.assertEqual(events[-1], {"type": "done", "requestId": "r1", "generation": 1,
                                      "hasMore": False, "count": 0})
        self.assertEqual(list(worker.events(request("feed.next")))[-1]["count"], 0)

    def test_missing_metadata_and_supported_kinds(self):
        entries = [video(1), {"_type": "url", "ie_key": "YoutubeTab", "id": "UC" + "x" * 22,
                              "url": "https://www.youtube.com/channel/UC" + "x" * 22},
                   {"_type": "url", "ie_key": "YoutubeTab", "id": "PL_fixture",
                    "url": "https://www.youtube.com/playlist?list=PL_fixture"},
                   None, {"_type": "url", "url": "https://example.invalid/"}]
        worker, _factory, _cursor = self.make_worker(entries)
        result = items(list(worker.events(request())))
        self.assertEqual([item["kind"] for item in result], ["video", "channel", "playlist"])
        self.assertEqual(result[0]["thumbnail"], "")
        self.assertIsNone(result[0]["duration"])
        self.assertIsNone(result[0]["viewCount"])
        self.assertIsNone(normalize_entry({**video(1), "duration": float("nan")})["duration"])

    def test_unsupported_items_and_duplicates_cannot_loop_forever(self):
        def endless():
            while True:
                yield {"_type": "url", "url": "https://example.invalid/"}
        worker, _factory, cursor = self.make_worker(endless(), scan_factor=1)
        events = list(worker.events(request()))
        self.assertEqual(events[-1]["code"], "unavailable")
        self.assertLessEqual(cursor.produced, 91)

    def test_options_prohibit_writes_and_individual_extraction(self):
        worker, factory, _cursor = self.make_worker([video(1)])
        list(worker.events(request()))
        options = factory.instances[0].options
        self.assertFalse(options["mark_watched"])
        self.assertFalse(options["getcomments"])
        self.assertTrue(options["skip_download"])
        self.assertFalse(options["cachedir"])
        self.assertEqual(options["extractor_retries"], 1)
        self.assertEqual(options["socket_timeout"], 12)

    def test_only_app_managed_cookie_lease_is_updated(self):
        with tempfile.TemporaryDirectory() as directory:
            original = Path(directory) / "original.txt"
            original.write_text("fixture input cookies", encoding="utf-8")
            lease = Path(directory) / "leased.txt"
            lease.write_text(original.read_text(encoding="utf-8"), encoding="utf-8")
            worker, factory, _cursor = self.make_worker([video(1)])
            list(worker.events(request(cookiePath=str(lease))))
            managed = Path(factory.instances[0].options["cookiefile"])
            self.assertEqual(managed, lease)
            self.assertEqual(managed.read_text(encoding="utf-8"), "fixture input cookies")
            worker.close()
            self.assertEqual(original.read_text(encoding="utf-8"), "fixture input cookies")
            self.assertEqual(managed.read_text(encoding="utf-8"), "updated managed cookies")

    def test_new_open_replaces_cursor_and_stale_generation_errors(self):
        cursors = []
        def resolver(_url):
            cursor = LazyFixture(video(i) for i in range(270))
            cursors.append(cursor)
            return {"_type": "playlist", "entries": cursor}
        factory = Factory(resolver)
        worker = FeedWorker(factory)
        self.addCleanup(worker.close)
        list(worker.events(request()))
        list(worker.events(request(source="subscriptions", generation=2)))
        self.assertTrue(cursors[0].closed)
        self.assertTrue(factory.instances[0].closed)
        self.assertEqual(list(worker.events(request("feed.next", generation=1)))[-1]["code"], "unavailable")
        self.assertEqual(len(items(list(worker.events(request("feed.next", generation=2))))), 90)

    def test_safe_error_classification_and_no_private_diagnostics(self):
        cases = [(RuntimeError("Sign in cookie SECRET"), "auth"),
                 (TimeoutError("PRIVATE host"), "network"),
                 (RuntimeError("Video unavailable SECRET"), "unavailable"),
                 (RuntimeError("Unexpected SECRET"), "internal")]
        for error, code in cases:
            with self.subTest(code=code):
                def resolver(_url):
                    print("PRIVATE upstream payload")
                    raise error
                worker = FeedWorker(Factory(resolver))
                stdout, stderr = io.StringIO(), io.StringIO()
                with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                    events = list(worker.events(request()))
                self.assertEqual(events[-1]["code"], code)
                self.assertNotIn("SECRET", json.dumps(events))
                self.assertEqual(stdout.getvalue() + stderr.getvalue(), "")

    def test_protocol_ping_close_invalid_and_ndjson(self):
        input_lines = "\n".join(json.dumps(value) for value in (
            request("ping"), request("feed.close"), {"command": "invalid", "requestId": "r3", "generation": 1})) + "\n{bad json}\n"
        stdout = io.StringIO()
        run(io.StringIO(input_lines), stdout, factory=Factory(lambda _url: None))
        events = [json.loads(line) for line in stdout.getvalue().splitlines()]
        self.assertEqual([event["type"] for event in events], ["error", "done", "error", "error"])
        self.assertEqual(events[0]["code"], "unsupported")

    def test_failure_during_continuation_is_categorized(self):
        def generator():
            yield video(1)
            raise TimeoutError("sensitive request URL")
        worker, _factory, _cursor = self.make_worker(generator())
        events = list(worker.events(request()))
        self.assertEqual(events[0]["type"], "item")
        self.assertEqual(events[-1]["type"], "done")
        self.assertEqual(events[-1]["count"], 1)
        self.assertFalse(events[-1]["hasMore"])
        self.assertEqual(events[-1]["warning"]["code"], "network")
        self.assertNotIn("sensitive request URL", json.dumps(events))

    def test_lookahead_failure_preserves_full_batch_and_closes_terminal_cursor(self):
        def generator():
            yield from (video(i) for i in range(90))
            raise TimeoutError("PRIVATE continuation URL")
        worker, factory, cursor = self.make_worker(generator())
        events = list(worker.events(request()))
        self.assertEqual([item["id"] for item in items(events)], [video(i)["id"] for i in range(90)])
        self.assertEqual(events[-1]["type"], "done")
        self.assertEqual(events[-1]["count"], 90)
        self.assertFalse(events[-1]["hasMore"])
        self.assertEqual(events[-1]["warning"]["code"], "network")
        self.assertTrue(cursor.closed)
        self.assertTrue(factory.instances[0].closed)
        self.assertNotIn("PRIVATE", json.dumps(events))
        next_events = list(worker.events(request("feed.next", request_id="r2")))
        self.assertEqual(items(next_events), [])
        self.assertEqual(next_events[-1]["code"], "unavailable")
        self.assertEqual(len(factory.instances[0].calls), 1)

    def test_later_batch_failure_retains_delivered_items_without_reopening(self):
        def generator():
            yield from (video(i) for i in range(95))
            raise TimeoutError("PRIVATE continuation URL")
        worker, factory, _cursor = self.make_worker(generator())
        first = list(worker.events(request()))
        second = list(worker.events(request("feed.next", request_id="r2")))
        self.assertTrue(first[-1]["hasMore"])
        self.assertEqual([item["id"] for item in items(first) + items(second)],
                         [video(i)["id"] for i in range(95)])
        self.assertEqual(second[-1]["type"], "done")
        self.assertEqual(second[-1]["count"], 5)
        self.assertFalse(second[-1]["hasMore"])
        self.assertEqual(second[-1]["warning"]["code"], "network")
        self.assertEqual(len(factory.instances[0].calls), 1)

    def test_zero_item_continuation_failure_remains_an_error(self):
        def generator():
            raise TimeoutError("PRIVATE continuation URL")
            yield video(1)
        worker, _factory, _cursor = self.make_worker(generator())
        events = list(worker.events(request()))
        self.assertEqual(events[-1]["type"], "error")
        self.assertEqual(events[-1]["code"], "network")


def cookie(domain, name):
    return http.cookiejar.Cookie(0, name, "fixture-value", None, False, domain, True,
                                domain.startswith("."), "/", True, True, None, True,
                                None, None, {}, False)


class RecommendationMetadataTests(unittest.TestCase):
    def test_legacy_menu_preserves_only_explicit_feedback_tokens(self):
        not_interested = feedback_menu_item(token="private-original-token")
        not_interested["menuServiceItemRenderer"]["serviceEndpoint"]["feedbackEndpoint"]["actions"] = [
            {"undoFeedbackEndpoint": {"undoToken": "private-undo-token"}}]
        renderer = {"title": "Not interested", "menu": {"menuRenderer": {"items": [
            not_interested, feedback_menu_item("dont_recommend_channel", "private-channel-token")]}}}
        result = extract_feedback_actions(renderer)
        self.assertEqual(set(result), {"not_interested", "dont_recommend_channel"})
        self.assertEqual(result["not_interested"], {"feedbackToken": "private-original-token",
                                                  "clickTrackingParams": "fixture-tracking"})
        self.assertEqual(result["dont_recommend_channel"]["feedbackToken"], "private-channel-token")
        self.assertNotIn("private-undo-token", json.dumps(result))

    def test_modern_inline_menu_uses_title_and_typed_endpoint(self):
        endpoint = feedback_menu_item()["menuServiceItemRenderer"]["serviceEndpoint"]
        renderer = {"metadata": {"lockupMetadataViewModel": {"menuButton": {"buttonViewModel": {
            "onTap": {"innertubeCommand": {"showSheetCommand": {"panelLoadingStrategy": {"inlineContent": {
                "sheetViewModel": {"content": {"listViewModel": {"listItems": [{"listItemViewModel": {
                    "title": {"content": "관심 없음"},
                    "rendererContext": {"commandContext": {"onTap": {"innertubeCommand": endpoint}}}}}]}}}}}}}}}}}}}
        result = extract_feedback_actions(renderer)
        self.assertEqual(set(result), {"not_interested"})
        self.assertEqual(result["not_interested"]["feedbackToken"], "fixture-token")

    def test_unclear_menus_other_writes_and_title_heuristics_have_no_actions(self):
        cases = [feedback_menu_item(icon="REMOVE", label="Remove from watch history"),
                 feedback_menu_item(icon="REMOVE", label="Remove from playlist"),
                 feedback_menu_item(icon="REMOVE", label="Delete"),
                 feedback_menu_item("dont_recommend_channel", icon="NOT_INTERESTED")]
        wrong_endpoint = feedback_menu_item()
        wrong_endpoint["menuServiceItemRenderer"]["serviceEndpoint"]["commandMetadata"]["webCommandMetadata"]["apiUrl"] = "/youtubei/v1/like/like"
        cases.append(wrong_endpoint)
        no_token = feedback_menu_item(token="")
        cases.append(no_token)
        overlong = feedback_menu_item(token="x" * 16385)
        cases.append(overlong)
        cases.append(feedback_menu_item(token="secret\nheader"))
        for item in cases:
            with self.subTest(item=item):
                self.assertEqual(extract_feedback_actions({"menu": {"menuRenderer": {"items": [item]}}}), {})
        self.assertEqual(extract_feedback_actions({"title": "Not interested", "feedbackEndpoint": {"feedbackToken": "unsafe"}}), {})

    def test_conflicting_menu_tokens_are_not_exposed(self):
        renderer = {"menu": {"menuRenderer": {"items": [feedback_menu_item(token="one"),
                                                           feedback_menu_item(token="other")]}}}
        self.assertEqual(extract_feedback_actions(renderer), {})


class FakeFeedbackExtractor:
    def __init__(self):
        self.calls = []
        self.header_calls = []
        self.response = {"feedbackResponses": [{"isProcessed": True}]}
        self.authorization = "fixture-signed-authorization"

    def _extract_video(self, renderer):
        return video(1)

    def _extract_lockup_view_model(self, renderer):
        return video(2)

    def _extract_data(self, *args, **kwargs):
        return {}, {"INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}}}

    def _download_ytcfg(self, *args, **kwargs):
        return {"INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}}}

    def generate_api_headers(self, *, ytcfg=None, **kwargs):
        self.header_calls.append((ytcfg, kwargs))
        return {"Authorization": self.authorization} if self.authorization else {}

    def _extract_context(self, config):
        return config["INNERTUBE_CONTEXT"]

    def _call_api(self, endpoint, query, video_id, **kwargs):
        self.calls.append((endpoint, query, video_id, kwargs))
        return self.response


class FakeFeedbackYDL:
    def __init__(self):
        self.extractor = FakeFeedbackExtractor()

    def get_info_extractor(self, name):
        if name != "YoutubeTab":
            raise AssertionError("unexpected individual-video extractor")
        return self.extractor


class RecommendationTransportTests(unittest.TestCase):
    def test_renderer_wrappers_preserve_actions_without_individual_extraction(self):
        ydl = FakeFeedbackYDL()
        RecommendationSession(ydl)
        renderer = {"menu": {"menuRenderer": {"items": [feedback_menu_item()]}}}
        for method in (ydl.extractor._extract_video, ydl.extractor._extract_lockup_view_model):
            result = method(renderer)
            self.assertEqual(result["_recommendation_endpoints"]["not_interested"]["feedbackToken"], "fixture-token")
        self.assertEqual(ydl.extractor.calls, [])

    def test_feedback_request_has_selected_channel_headers_context_and_exact_token(self):
        account = {"id": "fixture-brand", "pageId": "123", "dataSyncId": "123||fixture-user", "authUser": 1}
        ydl = FakeFeedbackYDL()
        session = RecommendationSession(ydl, account)
        ydl.extractor._extract_data("fixture")
        payload = {"feedbackToken": "original-private-feedback", "clickTrackingParams": "original-tracking"}
        self.assertTrue(session.send(payload, video(1)["id"]))
        self.assertEqual(len(ydl.extractor.calls), 1)
        endpoint, query, video_id, kwargs = ydl.extractor.calls[0]
        self.assertEqual((endpoint, query["feedbackTokens"], video_id),
                         ("feedback", ["original-private-feedback"], video(1)["id"]))
        self.assertFalse(query["isFeedbackTokenUnencrypted"])
        self.assertFalse(query["shouldMerge"])
        self.assertEqual(kwargs["context"]["user"]["onBehalfOfUser"], "123")
        self.assertEqual(kwargs["context"]["clickTracking"]["clickTrackingParams"], "original-tracking")
        cfg, headers_kwargs = ydl.extractor.header_calls[0]
        self.assertEqual(cfg["DATASYNC_ID"], account["dataSyncId"])
        self.assertEqual(headers_kwargs, {"delegated_session_id": "123", "user_session_id": "fixture-user", "session_index": 1})
        self.assertNotIn("clickTracking", session.config["INNERTUBE_CONTEXT"])

    def test_feedback_requires_signed_auth_and_explicit_server_confirmation(self):
        ydl = FakeFeedbackYDL()
        session = RecommendationSession(ydl)
        ydl.extractor._extract_data("fixture")
        ydl.extractor.authorization = ""
        with self.assertRaises(RecommendationError) as error:
            session.send({"feedbackToken": "private-token"}, video(1)["id"])
        self.assertEqual(error.exception.code, "auth")
        self.assertEqual(ydl.extractor.calls, [])
        ydl.extractor.authorization = "fixture-auth"
        for response in ({}, {"feedbackResponses": []}, {"feedbackResponses": [{"isProcessed": False}]},
                         {"feedbackResponses": [{"isProcessed": True}, {}]},
                         {"feedbackResponses": [{"isProcessed": 1}]},
                         {"error": {"message": "private-response"}}):
            with self.subTest(response=response):
                ydl.extractor.response = response
                with self.assertRaises(RecommendationError) as error:
                    session.send({"feedbackToken": "private-token"}, video(1)["id"])
                self.assertEqual(error.exception.code, "feedback")


class ExportSessionTests(unittest.TestCase):
    def test_domain_filter_resists_lookalike_domains(self):
        for domain in ("youtube.com", ".youtube.com", "m.youtube.com", "google.com", ".google.com",
                       "accounts.google.com", "accounts.youtube.com"):
            self.assertTrue(permitted_domain(domain), domain)
        for domain in ("example.com", "notgoogle.com", "google.com.evil.invalid", "youtube.com.evil.invalid"):
            self.assertFalse(permitted_domain(domain), domain)

    def test_only_filtered_cookies_are_saved(self):
        source = [cookie(".youtube.com", "youtube"), cookie("accounts.google.com", "google"),
                  cookie("private.example.com", "unrelated")]
        observed = []
        def extractor(browser, *, profile, logger):
            observed.append((browser, profile))
            logger.warning("private cookie value")
            return iter(source)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "session.txt"
            result = export_session("fixture-profile", output, extractor=extractor,
                                    jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertEqual(result, {"success": True, "count": 2})
            text = output.read_text(encoding="utf-8")
            self.assertIn("youtube.com", text)
            self.assertIn("accounts.google.com", text)
            self.assertNotIn("private.example.com", text)
            self.assertNotIn("unrelated", text)
            self.assertEqual(observed, [("firefox", "fixture-profile")])
            self.assertEqual(len(list(Path(directory).iterdir())), 1)

    def test_zero_permitted_cookies_preserves_existing_output(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "session.txt"
            output.write_text("prior filtered session", encoding="utf-8")
            with self.assertRaises(ValueError):
                export_session("fixture", output, extractor=lambda *args, **kwargs: [cookie("example.com", "unrelated")],
                               jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertEqual(output.read_text(encoding="utf-8"), "prior filtered session")
            self.assertEqual(len(list(Path(directory).iterdir())), 1)

    def test_stale_work_directory_does_not_block_export_or_delete_existing_files(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "session.txt"
            work = output.with_suffix(".work")
            work.mkdir()
            stale = work / "stale-copy.sqlite"
            stale.write_text("preserved stale fixture", encoding="utf-8")
            result = export_session("fixture", output,
                                    extractor=lambda *args, **kwargs: [cookie("youtube.com", "youtube")],
                                    jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertEqual(result, {"success": True, "count": 1})
            self.assertEqual(stale.read_text(encoding="utf-8"), "preserved stale fixture")
            self.assertEqual(list(work.iterdir()), [stale])
            self.assertIn("youtube.com", output.read_text(encoding="utf-8"))

    def test_work_path_file_is_preserved_and_rejected_before_extraction(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "session.txt"
            work = output.with_suffix(".work")
            work.write_text("unrelated fixture", encoding="utf-8")
            with self.assertRaises((ValueError, OSError)):
                export_session("fixture", output, extractor=lambda *args, **kwargs: self.fail("extractor called"),
                               jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertEqual(work.read_text(encoding="utf-8"), "unrelated fixture")
            self.assertFalse(output.exists())

    def test_work_path_link_is_preserved_and_rejected_before_extraction(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "session.txt"
            work = output.with_suffix(".work")
            work.mkdir()
            sentinel = work / "sentinel.txt"
            sentinel.write_text("unrelated fixture", encoding="utf-8")
            # Covers the rejection path on hosts where creating links needs admin rights.
            with patch.object(Path, "is_symlink", autospec=True, side_effect=lambda path: path == work):
                with self.assertRaises(ValueError):
                    export_session("fixture", output, extractor=lambda *args, **kwargs: self.fail("extractor called"),
                                   jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertEqual(sentinel.read_text(encoding="utf-8"), "unrelated fixture")
            self.assertFalse(output.exists())

    def test_failed_export_cleans_only_owned_files_and_restores_tempdir(self):
        previous = tempfile.tempdir
        observed = []
        def extractor(*args, **kwargs):
            observed.append(Path(tempfile.tempdir))
            (Path(tempfile.tempdir) / "copy.sqlite").write_text("fixture", encoding="utf-8")
            raise RuntimeError("fixture extractor failure")
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "session.txt"
            with self.assertRaises(RuntimeError):
                export_session("fixture", output, extractor=extractor,
                               jar_factory=http.cookiejar.MozillaCookieJar)
            self.assertFalse(observed[0].exists())
            self.assertFalse(output.with_suffix(".work").exists())
            self.assertEqual(tempfile.tempdir, previous)
            self.assertEqual(list(Path(directory).iterdir()), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
