using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DryIoc.ImTools;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests.NewznabTests
{
    [TestFixture]
    public class NewznabFixture : CoreTest<Newznab>
    {
        private NewznabCapabilities _caps;

        [SetUp]
        public void Setup()
        {
            Subject.Definition = new IndexerDefinition()
                {
                    Id = 5,
                    Name = "Newznab",
                    Settings = new NewznabSettings()
                        {
                            BaseUrl = "http://indexer.local/",
                            Categories = new int[] { 1 }
                        }
                };

            _caps = new NewznabCapabilities();
            Mocker.GetMock<INewznabCapabilitiesProvider>()
                .Setup(v => v.GetCapabilities(It.IsAny<NewznabSettings>()))
                .Returns(_caps);
        }

        [Test]
        public async Task should_parse_recent_feed_from_newznab_nzb_su()
        {
            var recentFeed = ReadAllText(@"Files/Indexers/Newznab/newznab_nzb_su.xml");

            Mocker.GetMock<IHttpClient>()
                .Setup(o => o.ExecuteAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Get)))
                .Returns<HttpRequest>(r => Task.FromResult(new HttpResponse(r, new HttpHeader(), recentFeed)));

            var releases = await Subject.FetchRecent();

            releases.Should().HaveCount(100);

            var releaseInfo = releases.First();

            releaseInfo.Title.Should().Be("White.Collar.S03E05.720p.HDTV.X264-DIMENSION");
            releaseInfo.DownloadProtocol.Should().Be(DownloadProtocol.Usenet);
            releaseInfo.DownloadUrl.Should().Be("http://nzb.su/getnzb/24967ef4c2e26296c65d3bbfa97aa8fe.nzb&i=37292&r=xxx");
            releaseInfo.InfoUrl.Should().Be("http://nzb.su/details/24967ef4c2e26296c65d3bbfa97aa8fe");
            releaseInfo.CommentUrl.Should().Be("http://nzb.su/details/24967ef4c2e26296c65d3bbfa97aa8fe#comments");
            releaseInfo.IndexerId.Should().Be(Subject.Definition.Id);
            releaseInfo.Indexer.Should().Be(Subject.Definition.Name);
            releaseInfo.PublishDate.Should().Be(DateTime.Parse("2012/02/27 16:09:39"));
            releaseInfo.Size.Should().Be(1183105773);
        }

        [Test]
        public async Task should_report_search_failure_to_search_criteria()
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(o => o.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ThrowsAsync(new WebException("Http request timed out", WebExceptionStatus.Timeout));

            var criteria = new SingleEpisodeSearchCriteria
            {
                Series = new Series { Title = "Series", TvdbId = 1 },
                SceneTitles = new List<string> { "Series" },
                Episodes = new List<Episode> { new Episode() },
                SeasonNumber = 1,
                EpisodeNumber = 1
            };

            var releases = await Subject.Fetch(criteria);

            releases.Should().BeEmpty();
            criteria.IndexerFailures[5].Should().BeOfType<WebException>();
        }

        private SingleEpisodeSearchCriteria GetEpisodeCriteria(int episodeNumber)
        {
            return new SingleEpisodeSearchCriteria
            {
                Series = new Series { Title = "Series", TvdbId = 1 },
                SceneTitles = new List<string> { "Series" },
                Episodes = new List<Episode> { new Episode() },
                SeasonNumber = 1,
                EpisodeNumber = episodeNumber
            };
        }

        private string GetSearchQueryKey(string apiKey, int episodeNumber = 1)
        {
            ((NewznabSettings)Subject.Definition.Settings).ApiKey = apiKey;

            return Subject.GetSearchQueryKey(GetEpisodeCriteria(episodeNumber));
        }

        [Test]
        public async Task should_count_sent_requests_to_search_criteria()
        {
            var recentFeed = ReadAllText(@"Files/Indexers/Newznab/newznab_nzb_su.xml");

            Mocker.GetMock<IHttpClient>()
                .Setup(o => o.ExecuteAsync(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(r => Task.FromResult(new HttpResponse(r, new HttpHeader(), recentFeed)));

            var criteria = GetEpisodeCriteria(1);

            await Subject.Fetch(criteria);

            var requestCount = Mocker.GetMock<IHttpClient>().Invocations.Count(i => i.Method.Name == nameof(IHttpClient.ExecuteAsync));

            requestCount.Should().BeGreaterThan(1);
            criteria.IndexerRequestDurations[5].Should().HaveCount(requestCount);
        }

        [Test]
        public void should_build_search_query_key_without_credentials_and_with_sorted_params()
        {
            var key = GetSearchQueryKey("secretkey");

            key.Should().StartWith("GET http://indexer.local/api?");
            key.Should().NotContain("secretkey").And.NotContain("apikey");
            key.Should().Be(GetSearchQueryKey("otherkey"));
            key.Should().NotBe(GetSearchQueryKey("secretkey", 2));

            foreach (var request in key.Split('\n').Where(l => l.StartsWith("GET ")))
            {
                var parameters = request.Split('?', 2)[1].Split('&');

                parameters.Should().BeInAscendingOrder(StringComparer.Ordinal);
            }
        }

        [Test]
        public async Task should_parse_recent_feed_from_newznab_animetosho()
        {
            var recentFeed = ReadAllText(@"Files/Indexers/Torznab/torznab_animetosho.xml");

            Mocker.GetMock<IHttpClient>()
                .Setup(o => o.ExecuteAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Get)))
                .Returns<HttpRequest>(r => Task.FromResult(new HttpResponse(r, new HttpHeader(), recentFeed)));

            var releases = await Subject.FetchRecent();

            releases.Should().HaveCount(1);

            releases.First().Should().BeOfType<ReleaseInfo>();
            var releaseInfo = releases.First() as ReleaseInfo;

            releaseInfo.Title.Should().Be("[HorribleSubs] Frame Arms Girl - 07 [720p].mkv");
            releaseInfo.DownloadProtocol.Should().Be(DownloadProtocol.Usenet);
            releaseInfo.DownloadUrl.Should().Be("http://storage.localhost/nzb/123452.nzb");
            releaseInfo.InfoUrl.Should().Be("https://localhost/view/horriblesubs-frame-arms-girl-07-720p-mkv.123452");
            releaseInfo.CommentUrl.Should().Be("https://localhost/view/horriblesubs-frame-arms-girl-07-720p-mkv.123452");
            releaseInfo.Indexer.Should().Be(Subject.Definition.Name);
            releaseInfo.PublishDate.Should().Be(DateTime.Parse("Mon, 15 May 2017 19:15:56 +0000").ToUniversalTime());
            releaseInfo.Size.Should().Be(473987489);
            releaseInfo.TvdbId.Should().Be(0);
            releaseInfo.TvRageId.Should().Be(0);
        }

        [Test]
        public void should_use_best_pagesize_reported_by_caps()
        {
            _caps.MaxPageSize = 30;
            _caps.DefaultPageSize = 25;

            Subject.PageSize.Should().Be(30);
        }

        [Test]
        public void should_not_use_pagesize_over_100_even_if_reported_in_caps()
        {
            _caps.MaxPageSize = 250;
            _caps.DefaultPageSize = 25;

            Subject.PageSize.Should().Be(100);
        }

        [Test]
        public async Task should_record_indexer_failure_if_caps_throw()
        {
            var request = new HttpRequest("http://my.indexer.com");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)429);
            response.Headers["Retry-After"] = "300";

            Mocker.GetMock<INewznabCapabilitiesProvider>()
                .Setup(v => v.GetCapabilities(It.IsAny<NewznabSettings>()))
                .Throws(new TooManyRequestsException(request, response));

            _caps.MaxPageSize = 30;
            _caps.DefaultPageSize = 25;

            var releases = await Subject.FetchRecent();

            releases.Should().BeEmpty();

            Mocker.GetMock<IIndexerStatusService>()
                  .Verify(v => v.RecordFailure(It.IsAny<int>(), TimeSpan.FromMinutes(5.0)), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public async Task should_parse_languages()
        {
            var recentFeed = ReadAllText(@"Files/Indexers/Newznab/newznab_language.xml");

            Mocker.GetMock<IHttpClient>()
                .Setup(o => o.ExecuteAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Get)))
                .Returns<HttpRequest>(r => Task.FromResult(new HttpResponse(r, new HttpHeader(), recentFeed)));

            var releases = await Subject.FetchRecent();

            releases.Should().HaveCount(100);

            releases[0].Languages.Should().BeEquivalentTo(new[] { Language.English, Language.Japanese });
            releases[1].Languages.Should().BeEquivalentTo(new[] { Language.English, Language.Spanish });
            releases[2].Languages.Should().BeEquivalentTo(new[] { Language.French });
        }

        [TestCase("no custom attributes")]
        [TestCase("prematch=1 attribute", IndexerFlags.Scene)]
        [TestCase("haspretime=1 attribute", IndexerFlags.Scene)]
        [TestCase("prematch=0 attribute")]
        [TestCase("haspretime=0 attribute")]
        [TestCase("nuked=1 attribute", IndexerFlags.Nuked)]
        [TestCase("nuked=0 attribute")]
        [TestCase("prematch=1 and nuked=1 attributes", IndexerFlags.Scene, IndexerFlags.Nuked)]
        [TestCase("haspretime=0 and nuked=0 attributes")]
        public async Task should_parse_indexer_flags(string releaseGuid, params IndexerFlags[] indexerFlags)
        {
            var feed = ReadAllText(@"Files/Indexers/Newznab/newznab_indexerflags.xml");

            Mocker.GetMock<IHttpClient>()
                .Setup(o => o.ExecuteAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Get)))
                .Returns<HttpRequest>(r => Task.FromResult(new HttpResponse(r, new HttpHeader(), feed)));

            var releases = await Subject.FetchRecent();

            var release = releases.Should().ContainSingle(r => r.Guid == releaseGuid).Subject;

            indexerFlags.ForEach(f => release.IndexerFlags.Should().HaveFlag(f));
        }
    }
}
