"""Offline feed thumbnail selection must agree with the desktop HTTPS CDN policy."""
import unittest

from feed_worker import thumbnail_url


class ThumbnailSelectionTests(unittest.TestCase):
    def test_https_candidate_precedes_direct_http(self):
        secure = "https://i.ytimg.com/vi/fixture/hqdefault.jpg"
        self.assertEqual(thumbnail_url({"thumbnail": "http://i.ytimg.com/vi/fixture/maxresdefault.jpg",
                                        "thumbnails": [{"url": secure, "width": 480}]}), secure)

    def test_https_candidate_precedes_larger_http_card(self):
        secure = "https://i.ytimg.com/vi/fixture/secure.jpg"
        self.assertEqual(thumbnail_url({"thumbnails": [
            {"url": secure, "width": 480},
            {"url": "http://i.ytimg.com/vi/fixture/insecure.jpg", "width": 640}]}), secure)

    def test_known_cdn_http_is_upgraded_only_when_https_is_absent(self):
        for host in ("i.ytimg.com", "yt3.ggpht.com", "lh3.googleusercontent.com"):
            with self.subTest(host=host):
                legacy = f"http://{host}:80/fixture.jpg?size=480"
                expected = f"https://{host}/fixture.jpg?size=480"
                self.assertEqual(thumbnail_url({"thumbnail": legacy}), expected)
                self.assertEqual(thumbnail_url({"thumbnails": [{"url": legacy, "width": 480}]}), expected)

    def test_unpermitted_authority_cannot_replace_a_permitted_candidate(self):
        secure = "https://i.ytimg.com/fixture.jpg"
        for bad in ("https://i.ytimg.com.evil.invalid/fixture.jpg", "http://arbitrary.invalid/fixture.jpg",
                    "https://@i.ytimg.com/fixture.jpg", "https://user@i.ytimg.com/fixture.jpg",
                    "https://i.ytimg.com:444/fixture.jpg", "http://i.ytimg.com:443/fixture.jpg",
                    "https://ytimg.com/fixture.jpg", "https://i.ytimg.com\\@evil.invalid/fixture.jpg",
                    " https://i.ytimg.com/fixture.jpg", "https://i.ytimg.com/fixture\n.jpg"):
            with self.subTest(url=bad):
                self.assertEqual(thumbnail_url({"thumbnail": bad, "thumbnails": [{"url": secure}]}), secure)
                self.assertEqual(thumbnail_url({"thumbnail": bad}), "")

    def test_secure_direct_and_card_sizing_are_preserved(self):
        direct = "https://i.ytimg.com/fixture-direct.jpg"
        self.assertEqual(thumbnail_url({"thumbnail": direct}), direct)
        card = "https://i.ytimg.com/fixture-card.jpg"
        self.assertEqual(thumbnail_url({"thumbnails": [
            {"url": "https://i.ytimg.com/fixture-large.jpg", "width": 1280},
            {"url": "https://i.ytimg.com/fixture-small.jpg", "width": 320},
            {"url": card, "width": 640}]}), card)


if __name__ == "__main__":
    unittest.main()
