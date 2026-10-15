"""Offline scoped channel URL/avatar/owner metadata regressions."""

import copy
import unittest

from channel_metadata import at, avatar_url, channel_url, enrich_entry, renderer_channel, watch_owner


CHANNEL = "UC" + "x" * 22
OTHER = "UC" + "y" * 22
AVATAR = "https://yt3.ggpht.com/fixture-owner-avatar"
OTHER_AVATAR = "https://yt3.ggpht.com/fixture-other-avatar"


def old_renderer(*, channel=CHANNEL, avatar_channel=CHANNEL):
    return {"shortBylineText": {"runs": [{"text": "Fixture owner", "navigationEndpoint": {
        "browseEndpoint": {"browseId": channel, "canonicalBaseUrl": "/@fixture"}}}]},
        "channelThumbnailSupportedRenderers": {"channelThumbnailWithLinkRenderer": {
            "navigationEndpoint": {"browseEndpoint": {"browseId": avatar_channel}},
            "thumbnail": {"thumbnails": [{"url": AVATAR}]}}},
        "thumbnail": {"thumbnails": [{"url": "https://i.ytimg.com/fixture-video-card.jpg"}]}}


def modern_renderer(*, channel=CHANNEL):
    return {"metadata": {"lockupMetadataViewModel": {"title": {"content": "Video card title"},
        "image": {"decoratedAvatarViewModel": {
            "avatar": {"avatarViewModel": {"image": {"sources": [{"url": AVATAR}]}}},
            "rendererContext": {"commandContext": {"onTap": {"innertubeCommand": {
                "browseEndpoint": {"browseId": channel, "canonicalBaseUrl": "/@fixture"}}}}}}},
        "metadata": {"contentMetadataViewModel": {"metadataRows": [{"metadataParts": [
            {"text": {"content": "Modern owner"}}]}]}}}},
        "contentImage": {"thumbnailViewModel": {"image": {"sources": [
            {"url": "https://i.ytimg.com/fixture-video-card.jpg"}]}}}}


def owner(*, channel=CHANNEL, title="Watch owner", avatar=AVATAR):
    return {"videoSecondaryInfoRenderer": {"owner": {"videoOwnerRenderer": {
        "navigationEndpoint": {"browseEndpoint": {"browseId": channel, "canonicalBaseUrl": "/@fixture"}},
        "title": {"runs": [{"text": title}]}, "thumbnail": {"thumbnails": [{"url": avatar}]}}}}}


def watch_response(contents):
    return {"contents": {"twoColumnWatchNextResults": {"results": {"results": {"contents": contents}}}}}


class ChannelMetadataTests(unittest.TestCase):
    def test_channel_urls_canonicalize_real_channel_forms_and_strip_queries(self):
        for value, expected in ((f"https://www.youtube.com/channel/{CHANNEL}", f"channel/{CHANNEL}"),
                                ("/@fixture", "@fixture"), ("https://youtube.com/@fixture/shorts?x=1#fragment", "@fixture"),
                                ("https://m.youtube.com/c/Fixture/streams", "c/Fixture"),
                                ("https://www.youtube.com/user/Fixture/playlists", "user/Fixture"),
                                ("https://www.youtube.com/@한글", "@한글")):
            with self.subTest(value=value):
                self.assertEqual(channel_url(value), "https://www.youtube.com/" + expected + "/videos")
        self.assertEqual(channel_url("https://evil.invalid/watch", CHANNEL), f"https://www.youtube.com/channel/{CHANNEL}/videos")

    def test_channel_urls_reject_arbitrary_hosts_videos_credentials_ports_and_encoded_separators(self):
        bad = [None, 1, True, "", "http://www.youtube.com/@fixture", "//www.youtube.com/@fixture",
               "https://www.youtube.com.evil.invalid/@fixture", "https://evil.invalid/@fixture",
               "https://user:pass@www.youtube.com/@fixture", "https://www.youtube.com:444/@fixture",
               "https://www.youtube.com:bad/@fixture", "https://www.youtube.com/watch?v=AAAAAAAAAAA",
               "https://www.youtube.com/shorts/AAAAAAAAAAA", "https://www.youtube.com/playlist?list=PLfixture",
               "https://www.youtube.com/@", "https://www.youtube.com/@fixture/other",
               "https://www.youtube.com/@fixture/videos/extra", "https://www.youtube.com/channel/UCinvalid",
               "https://www.youtube.com/@fixture%2fother", "https://www.youtube.com/@fixture%0a",
               "https://www.youtube.com/@fixture%3fquery", "https://www.youtube.com/@fixture\\other",
               "https://www.youtube.com/@" + "x" * 129]
        for value in bad:
            with self.subTest(value=value):
                self.assertEqual(channel_url(value), "")

    def test_old_renderer_and_matching_avatar_metadata(self):
        renderer = old_renderer()
        expected = {"channel": "Fixture owner", "channel_id": CHANNEL,
                    "channel_url": f"https://www.youtube.com/channel/{CHANNEL}/videos", "channel_thumbnail": AVATAR}
        self.assertEqual(renderer_channel(renderer), expected)
        for field in ("longBylineText", "ownerText"):
            moved = copy.deepcopy(renderer)
            moved[field] = moved.pop("shortBylineText")
            self.assertEqual(renderer_channel(moved), expected)

    def test_modern_decorated_avatar_resolves_plain_owner_row_without_using_video_thumbnail(self):
        result = renderer_channel(modern_renderer())
        self.assertEqual((result["channel_id"], result["channel"], result["channel_thumbnail"]),
                         (CHANNEL, "Modern owner", AVATAR))
        self.assertEqual(result["channel_url"], f"https://www.youtube.com/channel/{CHANNEL}/videos")
        renderer = modern_renderer()
        del renderer["metadata"]["lockupMetadataViewModel"]["image"]
        self.assertNotIn("channel_thumbnail", renderer_channel(renderer))
        self.assertNotIn("channel_thumbnail", renderer_channel({"thumbnail": {"thumbnails": [{"url": OTHER_AVATAR}]}}))

    def test_mismatched_byline_avatar_and_entry_identity_do_not_leak_neighbor_metadata(self):
        renderer = old_renderer(avatar_channel=OTHER)
        result = renderer_channel(renderer)
        self.assertEqual(result["channel_id"], CHANNEL)
        self.assertNotIn("channel_thumbnail", result)
        entry = {"id": "AAAAAAAAAAA", "channel_id": CHANNEL}
        self.assertNotIn("channel_thumbnail", enrich_entry(entry, renderer))
        other_entry = {"id": "BBBBBBBBBBB", "channel_id": OTHER, "channel": "Existing other owner"}
        self.assertEqual(enrich_entry(other_entry, old_renderer()), other_entry)

    def test_enrichment_preserves_existing_fields_and_fills_only_missing_matching_metadata(self):
        entry = {"id": "AAAAAAAAAAA", "channel_id": CHANNEL, "channel": "Existing owner",
                 "channel_thumbnail": OTHER_AVATAR, "duration": 0, "live_status": False}
        result = enrich_entry(entry, old_renderer())
        self.assertEqual(result["channel"], "Existing owner")
        self.assertEqual(result["channel_thumbnail"], OTHER_AVATAR)
        self.assertEqual((result["duration"], result["live_status"]), (0, False))
        self.assertEqual(result["channel_url"], f"https://www.youtube.com/channel/{CHANNEL}/videos")
        self.assertEqual(enrich_entry({"channel": "", "channel_id": None}, old_renderer())["channel"], "Fixture owner")
        self.assertEqual(enrich_entry(None, old_renderer()), None)
        self.assertEqual(entry["channel_thumbnail"], OTHER_AVATAR)

    def test_watch_owner_uses_only_primary_video_owner_subtree(self):
        response = watch_response([{"videoPrimaryInfoRenderer": {"title": {"runs": [{"text": "Video title"}]}}}, owner()])
        response["contents"]["twoColumnWatchNextResults"]["secondaryResults"] = {"secondaryResults": {
            "results": [owner(channel=OTHER, avatar=OTHER_AVATAR)]}}
        response["engagementPanels"] = [owner(channel=OTHER, avatar=OTHER_AVATAR)]
        response["playerResponse"] = {"videoDetails": {"channelId": OTHER, "author": "Neighbor"}}
        self.assertEqual(watch_owner(response), {"id": CHANNEL, "url": f"https://www.youtube.com/channel/{CHANNEL}/videos",
                                               "title": "Watch owner", "thumbnail": AVATAR})
        del response["contents"]["twoColumnWatchNextResults"]["results"]
        self.assertIsNone(watch_owner(response))

    def test_watch_owner_does_not_stringify_malformed_runs_or_use_invalid_browse(self):
        for malformed in (None, False, 1, "bad", [], {}, {"browseId": 1}, {"browseId": "UCinvalid"}):
            response = watch_response([owner()])
            response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoSecondaryInfoRenderer"]["owner"]["videoOwnerRenderer"]["navigationEndpoint"]["browseEndpoint"] = malformed
            with self.subTest(endpoint=malformed):
                self.assertIsNone(watch_owner(response))
        for runs in (None, False, True, 1, "bad", {}, [None, {}, {"text": {"private": "object"}}, {"text": "Only text"}]):
            response = watch_response([owner()])
            response["contents"]["twoColumnWatchNextResults"]["results"]["results"]["contents"][0]["videoSecondaryInfoRenderer"]["owner"]["videoOwnerRenderer"]["title"]["runs"] = runs
            with self.subTest(runs=runs):
                result = watch_owner(response)
                self.assertEqual(result["title"], "Only text" if isinstance(runs, list) else "")

    def test_malformed_renderer_nodes_and_bounded_avatar_walk_are_safe(self):
        malformed = [None, False, 1, "bad", [], {}, {"shortBylineText": {"runs": True}},
                     {"shortBylineText": {"runs": [None, 1, {}, {"navigationEndpoint": "bad"}]}},
                     {"metadata": {"lockupMetadataViewModel": {"image": {"decoratedAvatarViewModel": None}}}}]
        for value in malformed:
            with self.subTest(value=value):
                self.assertEqual(renderer_channel(value), {})
        for value in (None, True, 1, "bad", {"url": "http://insecure.invalid/image"}, {"url": "https://" + "x" * 4096}):
            self.assertEqual(avatar_url(value), "")
        cyclic = {}
        cyclic["cycle"] = cyclic
        self.assertEqual(avatar_url(cyclic), "")
        nested = {"url": AVATAR}
        for _ in range(9):
            nested = {"child": nested}
        self.assertEqual(avatar_url(nested), "")
        self.assertEqual(at({"value": False}, "value", "bad"), None)


if __name__ == "__main__":
    unittest.main()
