"""Protocol and cursor tests; no YouTube requests or cookies are used."""

import unittest

from shorts_feed import ShortsFeed, ShortsFeedError, _flat_entry


A = "AAAAAAAAAAA"
B = "BBBBBBBBBBB"
C = "CCCCCCCCCCC"


def command(video_id, **extra):
    return {"reelWatchEndpoint": {"videoId": video_id,
                                  "thumbnail": {"thumbnails": [{"url": f"https://i.ytimg.com/vi/{video_id}/frame0.jpg", "width": 720}]},
                                  **extra}}


def page(ids, token=None):
    result = {"entries": [{"command": command(video_id)} for video_id in ids]}
    if token:
        result["continuationEndpoint"] = {"continuationCommand": {"token": token}}
    return result


class FakeExtractor:
    def __init__(self, _, *, authenticated=True, config=None, responses=None):
        self.is_authenticated = authenticated
        self.config = config if config is not None else {"LOGGED_IN": True, "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}}}
        self.responses = list(responses or [])
        self.calls = []
        self.config_calls = 0

    def initialize(self):
        pass

    def _download_ytcfg(self, client, item):
        self.config_calls += 1
        return self.config

    def generate_api_headers(self, **kwargs):
        self.header_arguments = kwargs
        return {"Authorization": "fake-auth"}

    def _extract_context(self, config):
        return config["INNERTUBE_CONTEXT"]

    def _call_api(self, endpoint, query, item, **kwargs):
        self.calls.append((endpoint, query, kwargs))
        if not self.responses:
            raise AssertionError("Unexpected API request")
        return self.responses.pop(0)


def fake_feed(sequence=(), *, initial=None, resolved=None, **kwargs):
    if resolved is None:
        resolved = {"endpoint": {"reelWatchEndpoint": {"params": "entrance", "inputType": "REEL_WATCH_INPUT_TYPE_BUTTON"}}}
    if initial is None:
        initial = {"replacementEndpoint": command(A), "sequenceContinuation": "next-1"}
    extractor = FakeExtractor(None, responses=[resolved, initial, *sequence], **kwargs)
    feed = ShortsFeed(object(), extractor_factory=lambda _: extractor)
    return feed, extractor


class ShortsFeedTests(unittest.TestCase):
    def test_lazy_bootstrap_uses_exact_feed_entrance_without_player(self):
        feed, extractor = fake_feed([page([B], "next-2"), page([C])])
        self.assertEqual(extractor.calls, [])
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual([call[0] for call in extractor.calls], ["navigation/resolve_url", "reel/reel_item_watch"])
        entrance = extractor.calls[1][1]
        self.assertTrue(entrance["disablePlayerResponse"])
        self.assertNotIn("playerRequest", entrance)
        self.assertNotIn("videoId", entrance)
        self.assertEqual(next(feed)["id"], B)
        self.assertEqual(extractor.calls[2][1], {"sequenceParams": "next-1"})
        self.assertEqual(next(feed)["id"], C)
        with self.assertRaises(StopIteration):
            next(feed)
        self.assertEqual(len(extractor.calls), 4)

    def test_account_cookies_required_before_any_request(self):
        feed, extractor = fake_feed(authenticated=False)
        with self.assertRaises(ShortsFeedError) as raised:
            next(feed)
        self.assertEqual(raised.exception.code, "auth")
        self.assertEqual(extractor.calls, [])
        self.assertEqual(extractor.config_calls, 0)

    def test_invalid_cookie_config_and_logged_out_api_rejected(self):
        feed, extractor = fake_feed(config={"LOGGED_IN": False})
        with self.assertRaises(ShortsFeedError) as raised:
            next(feed)
        self.assertEqual(raised.exception.code, "auth")
        self.assertEqual(extractor.calls, [])
        feed, _ = fake_feed(resolved={"responseContext": {"mainAppWebResponseContext": {"loggedOut": True}}})
        with self.assertRaises(ShortsFeedError) as raised:
            next(feed)
        self.assertEqual(raised.exception.code, "auth")

    def test_nested_comment_token_never_used_as_sequence(self):
        initial = {"replacementEndpoint": command(A), "engagementPanels": [{"continuationCommand": {"token": "comment-token"}}]}
        feed, extractor = fake_feed(initial=initial)
        self.assertEqual([item["id"] for item in feed], [A])
        self.assertEqual(len(extractor.calls), 2)

    def test_selected_identity_headers_and_body_are_consistent(self):
        config = {"LOGGED_IN": True, "DELEGATED_SESSION_ID": "999",
                  "USER_SESSION_ID": "old", "INNERTUBE_CONTEXT": {"client": {"clientName": "WEB"}, "user": {"onBehalfOfUser": "999"}}}
        account = {"id": "fixture", "pageId": "123", "dataSyncId": "123||user", "authUser": 1}
        feed, extractor = fake_feed(config=config)
        feed.account_context = account
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(extractor.header_arguments["delegated_session_id"], "123")
        self.assertEqual(extractor.header_arguments["user_session_id"], "user")
        self.assertEqual(extractor.header_arguments["session_index"], 1)
        self.assertEqual(extractor.header_arguments["ytcfg"]["DELEGATED_SESSION_ID"], "123")
        for _, _, kwargs in extractor.calls:
            self.assertEqual(kwargs["context"]["user"]["onBehalfOfUser"], "123")

    def test_duplicates_across_pages_and_repeated_token_stop(self):
        feed, extractor = fake_feed([page([A, B, B], "next-2"), page([B, C], "next-1")])
        self.assertEqual([item["id"] for item in feed], [A, B, C])
        self.assertEqual(len(extractor.calls), 4)

    def test_empty_or_duplicate_pages_have_bounded_requests(self):
        feed, extractor = fake_feed([page([], "next-2"), page([A], "next-3"), page([], "next-4"), page([B])])
        self.assertEqual(next(feed)["id"], A)
        with self.assertRaises(ShortsFeedError) as raised:
            next(feed)
        self.assertEqual(raised.exception.code, "unavailable")
        self.assertEqual(len(extractor.calls), 5)

    def test_total_page_cap_requires_refresh(self):
        feed, extractor = fake_feed([page([B], "next-2"), page([C])])
        feed.max_pages = 1
        self.assertEqual(next(feed)["id"], A)
        self.assertEqual(next(feed)["id"], B)
        with self.assertRaises(ShortsFeedError):
            next(feed)
        self.assertEqual(len(extractor.calls), 3)

    def test_arbitrary_seed_rejected(self):
        feed, extractor = fake_feed(resolved={"endpoint": command(A)})
        with self.assertRaises(ShortsFeedError):
            next(feed)
        self.assertEqual(len(extractor.calls), 1)

    def test_placeholder_metadata_and_matching_prefetch_only(self):
        self.assertEqual(_flat_entry(command(A))["title"], "Shorts 영상")
        prefetched = {"playerResponse": {"videoDetails": {"videoId": A, "title": "Fixture title", "author": "Fixture channel", "channelId": "UCfixture", "lengthSeconds": "37"}}}
        item = _flat_entry(command(A, unserializedPrefetchData=prefetched))
        self.assertEqual((item["title"], item["channel"], item["duration"]), ("Fixture title", "Fixture channel", 37))
        item = _flat_entry(command(B, unserializedPrefetchData=prefetched))
        self.assertEqual((item["title"], item["channel"]), ("Shorts 영상", ""))
        self.assertNotIn("duration", item)

    def test_only_current_overlay_menu_exposed_for_feedback(self):
        menu = {"menuRenderer": {"items": []}}
        renderer = {"replacementEndpoint": command(A), "overlay": {"reelPlayerOverlayRenderer": {"menu": menu}},
                    "engagementPanels": [{"menuRenderer": {"items": [{"unrelated": True}]}}]}
        entry = _flat_entry(command(A), item_response=renderer)
        self.assertEqual(entry["_recommendation_renderer"], {"menu": menu})
        mismatch = {"playerResponse": {"videoDetails": {"videoId": B}}, "reelItemWatchResponse": renderer}
        self.assertNotIn("_recommendation_renderer", _flat_entry(command(A, unserializedPrefetchData=mismatch)))
        unknown = {"reelItemWatchResponse": renderer}
        self.assertNotIn("_recommendation_renderer", _flat_entry(command(A, unserializedPrefetchData=unknown)))

    def test_invalid_items_and_close_do_not_make_extra_requests(self):
        self.assertIsNone(_flat_entry(command("invalid")))
        self.assertEqual(_flat_entry(command(A, thumbnail=None))["thumbnails"], [])
        feed, extractor = fake_feed([page([B])])
        self.assertEqual(next(feed)["id"], A)
        feed.close()
        with self.assertRaises(StopIteration):
            next(feed)
        self.assertEqual(len(extractor.calls), 2)


if __name__ == "__main__":
    unittest.main()
