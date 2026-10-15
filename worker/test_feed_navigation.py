"""Offline FeedWorker navigation integration; no cookies, extraction or network."""

import contextlib
import copy
import io
import json
import unittest

from feed_worker import FeedWorker, normalize_entry
from home_feed import HomeFeedError


CHANNEL = "UC" + "x" * 22
ACCOUNT = {"id": "fixture-brand", "pageId": "123", "dataSyncId": "123||fixture-user", "authUser": 1}


def video(index, **fields):
    return {"_type": "url", "ie_key": "Youtube", "id": f"v{index:010d}",
            "title": f"Video {index}", "channel_id": CHANNEL, **fields}


def request(command="feed.open", *, generation=1, source="home", **fields):
    return {"command": command, "requestId": f"fixture-{command}-{generation}", "generation": generation,
            "source": source, "cookiePath": None, "accountContext": ACCOUNT, "batchSize": 90, **fields}


def items(events):
    return [event["item"] for event in events if event["type"] == "item"]


class FakeYdl:
    def __init__(self, options):
        self.options = copy.deepcopy(options)
        self.extract_calls = []
        self.close_count = 0

    def extract_info(self, *args, **kwargs):
        self.extract_calls.append((args, kwargs))
        raise AssertionError("Navigation must not extract individual videos or playlists")

    def close(self):
        self.close_count += 1


class FakeNavigator:
    def __init__(self, entries=(), *, selected_entries=(), initialize_error=None, select_error=None):
        self.iterator = iter(entries)
        self.selected_entries = list(selected_entries)
        self.initialize_error, self.select_error = initialize_error, select_error
        self.produced = self.initialize_count = self.close_count = 0
        self.select_calls = []
        self.metadata = {"title": "Server home", "filterKind": "home", "filters": [
            {"id": "all-handle", "label": "All", "selected": True},
            {"id": "music-handle", "label": "Music", "selected": False}]}

    def initialize(self):
        self.initialize_count += 1
        print("PRIVATE initialization output")
        if self.initialize_error:
            raise self.initialize_error
        return self.metadata

    def __iter__(self):
        return self

    def __next__(self):
        value = next(self.iterator)
        self.produced += 1
        return value

    def select_filter(self, handle):
        self.select_calls.append(handle)
        print("PRIVATE filter output")
        if self.select_error:
            raise self.select_error
        if handle not in ("all-handle", "music-handle"):
            raise HomeFeedError("unsupported")
        self.iterator = iter(self.selected_entries)
        self.metadata = {"title": "Server Music", "filterKind": "home", "filters": [
            {"id": "new-all-handle", "label": "All", "selected": False},
            {"id": "new-music-handle", "label": "Music", "selected": True}]}

    def close(self):
        self.close_count += 1
        print("PRIVATE close output")


class FeedNavigationTests(unittest.TestCase):
    def make_worker(self, entries=(), **navigator_kwargs):
        navigator = FakeNavigator(entries, **navigator_kwargs)
        ydl_instances, navigation_calls = [], []

        def factory(options):
            ydl = FakeYdl(options)
            ydl_instances.append(ydl)
            return ydl

        def navigation_factory(ydl, source, url, account):
            navigation_calls.append((ydl, source, url, copy.deepcopy(account)))
            return navigator

        worker = FeedWorker(factory, navigation_factory=navigation_factory, clock=lambda: 0)
        self.addCleanup(worker.close)
        return worker, navigator, ydl_instances, navigation_calls

    def test_metadata_precedes_items_and_navigation_factory_receives_selected_context(self):
        worker, navigator, instances, calls = self.make_worker([video(0)])
        stream = worker.events(request())
        first = next(stream)
        self.assertEqual((first["type"], first["generation"]), ("metadata", 1))
        self.assertEqual(first["metadata"], navigator.metadata)
        self.assertEqual(navigator.produced, 0)
        rest = list(stream)
        self.assertEqual([event["type"] for event in rest], ["item", "done"])
        self.assertEqual(calls, [(instances[0], "home", "https://www.youtube.com/feed/recommended", ACCOUNT)])
        self.assertEqual(navigator.initialize_count, 1)
        self.assertEqual(instances[0].extract_calls, [])

    def test_three_90_batches_keep_lookahead_and_all_270_items(self):
        worker, navigator, instances, _ = self.make_worker(video(i) for i in range(270))
        batches = [list(worker.events(request()))]
        self.assertEqual(navigator.produced, 91)
        self.assertEqual(worker.pending["id"], video(90)["id"])
        batches.append(list(worker.events(request("feed.next"))))
        self.assertEqual(navigator.produced, 181)
        self.assertEqual(worker.pending["id"], video(180)["id"])
        batches.append(list(worker.events(request("feed.next"))))
        self.assertEqual([value["id"] for batch in batches for value in items(batch)], [video(i)["id"] for i in range(270)])
        self.assertEqual([batch[-1]["count"] for batch in batches], [90, 90, 90])
        self.assertEqual([batch[-1]["hasMore"] for batch in batches], [True, True, False])
        self.assertEqual(sum(event["type"] == "metadata" for batch in batches for event in batch), 1)
        self.assertEqual((len(instances), navigator.initialize_count), (1, 1))
        self.assertEqual(instances[0].extract_calls, [])

    def test_select_replaces_generation_pending_dedupe_and_feedback_handles_in_same_session(self):
        endpoint = {"not_interested": {"feedbackToken": "PRIVATE-original-token"}}
        worker, navigator, instances, calls = self.make_worker(
            [video(i, _recommendation_endpoints=endpoint) for i in range(95)],
            selected_entries=[video(0), video(0), video(90), video(96)])
        first = list(worker.events(request()))
        self.assertEqual(worker.pending["id"], video(90)["id"])
        self.assertTrue(worker.feedback_handles)
        events = list(worker.events(request("feed.select", generation=2, previousGeneration=1, filterId="music-handle")))
        self.assertEqual([value["id"] for value in items(events)], [video(i)["id"] for i in (0, 90, 96)])
        self.assertEqual(events[0]["type"], "metadata")
        self.assertTrue(events[0]["metadata"]["filters"][1]["selected"])
        self.assertEqual({event["generation"] for event in events}, {2})
        self.assertEqual(navigator.select_calls, ["music-handle"])
        self.assertEqual((len(instances), len(calls), instances[0].close_count), (1, 1, 0))
        self.assertEqual(worker.feedback_handles, {})
        self.assertIsNone(worker.pending)
        self.assertNotIn("PRIVATE-original-token", json.dumps(first + events))
        stale = list(worker.events(request("feed.next", generation=1)))
        self.assertEqual(stale[-1]["code"], "unavailable")
        self.assertEqual(worker.generation, 2)

    def test_select_validation_prevents_adapter_calls_for_stale_source_and_account(self):
        cases = [{"previousGeneration": 0}, {"previousGeneration": True}, {"generation": 1},
                 {"source": "subscriptions"}, {"accountContext": None},
                 {"accountContext": {**ACCOUNT, "id": "different-channel"}},
                 {"filterId": None}, {"filterId": ""}, {"filterId": "x" * 129}]
        for fields in cases:
            with self.subTest(fields=fields):
                worker, navigator, instances, _ = self.make_worker([video(0), video(1)])
                list(worker.events(request(batchSize=1)))
                selection = request("feed.select", generation=2, previousGeneration=1, filterId="music-handle")
                selection.update(fields)
                result = list(worker.events(selection))
                self.assertEqual(result[-1]["type"], "error")
                self.assertIn(result[-1]["code"], ("unavailable", "unsupported"))
                self.assertEqual(navigator.select_calls, [])
                self.assertEqual(navigator.close_count, 1)
                self.assertEqual(instances[0].close_count, 1)

    def test_unknown_and_failed_filter_close_resources_without_upstream_diagnostics(self):
        for failure, handle, code in ((HomeFeedError("auth"), "music-handle", "auth"),
                                     (TimeoutError("PRIVATE token/path"), "music-handle", "network"),
                                     (None, "unknown-handle", "unsupported")):
            with self.subTest(code=code):
                worker, navigator, instances, _ = self.make_worker([video(0), video(1)], select_error=failure)
                output, errors = io.StringIO(), io.StringIO()
                with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
                    list(worker.events(request(batchSize=1)))
                    result = list(worker.events(request("feed.select", generation=2, previousGeneration=1, filterId=handle)))
                self.assertEqual((result[-1]["type"], result[-1]["code"]), ("error", code))
                self.assertEqual((navigator.close_count, instances[0].close_count), (1, 1))
                self.assertIsNone(worker.entries)
                self.assertIsNone(worker.account_context)
                self.assertNotIn("PRIVATE", json.dumps(result))
                self.assertEqual(output.getvalue() + errors.getvalue(), "")

    def test_initialize_and_late_navigation_failures_close_without_double_close(self):
        worker, navigator, instances, _ = self.make_worker(initialize_error=HomeFeedError("auth"))
        result = list(worker.events(request()))
        self.assertEqual((result[-1]["type"], result[-1]["code"]), ("error", "auth"))
        self.assertEqual((navigator.close_count, instances[0].close_count), (1, 1))
        worker.close()
        self.assertEqual((navigator.close_count, instances[0].close_count), (1, 1))

        def failing():
            yield video(0)
            raise TimeoutError("PRIVATE continuation URL")

        worker, navigator, instances, _ = self.make_worker(failing())
        result = list(worker.events(request()))
        self.assertEqual([event["type"] for event in result], ["metadata", "item", "done"])
        self.assertEqual((result[-1]["count"], result[-1]["hasMore"], result[-1]["warning"]["code"]), (1, False, "network"))
        self.assertEqual((navigator.close_count, instances[0].close_count), (1, 1))
        self.assertNotIn("PRIVATE", json.dumps(result))

    def test_navigation_options_never_mark_watched_or_resolve_card_details(self):
        worker, _, instances, _ = self.make_worker([video(0)])
        list(worker.events(request()))
        options = instances[0].options
        self.assertFalse(options["mark_watched"])
        self.assertTrue(options["extract_flat"])
        self.assertTrue(options["skip_download"])
        self.assertFalse(options["getcomments"])
        self.assertFalse(options["cachedir"])
        self.assertEqual(options["js_runtimes"], {})
        self.assertIn("webpage", options["extractor_args"]["youtube"]["player_skip"])
        self.assertEqual(options["extractor_args"]["youtube"]["data_sync_id"], [ACCOUNT["dataSyncId"]])
        self.assertEqual(instances[0].extract_calls, [])

    def test_normalized_channel_navigation_uses_matching_renderer_metadata(self):
        renderer = {"shortBylineText": {"runs": [{"text": "Fixture owner", "navigationEndpoint": {
            "browseEndpoint": {"browseId": CHANNEL, "canonicalBaseUrl": "/@fixture"}}}]},
            "channelThumbnailSupportedRenderers": {"channelThumbnailWithLinkRenderer": {
                "navigationEndpoint": {"browseEndpoint": {"browseId": CHANNEL}},
                "thumbnail": {"thumbnails": [{"url": "https://yt3.ggpht.com/fixture-avatar"}]}}}}
        worker, _, _, _ = self.make_worker([video(0, _recommendation_renderer=renderer)])
        item = items(list(worker.events(request())))[0]
        self.assertEqual((item["channel"], item["channelId"], item["channelUrl"], item["channelThumbnail"]),
                         ("Fixture owner", CHANNEL, f"https://www.youtube.com/channel/{CHANNEL}/videos", "https://yt3.ggpht.com/fixture-avatar"))
        self.assertEqual(normalize_entry(video(1, uploader_url="https://www.youtube.com/@fallback"))["channelUrl"],
                         f"https://www.youtube.com/channel/{CHANNEL}/videos")


if __name__ == "__main__":
    unittest.main()
