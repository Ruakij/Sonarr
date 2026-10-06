using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Blocklisting
{
    [TestFixture]
    public class BlocklistRepositoryFixture : DbTest<BlocklistRepository, Blocklist>
    {
        private Blocklist _blocklist;
        private Series _series1;
        private Series _series2;

        [SetUp]
        public void Setup()
        {
            _blocklist = new Blocklist
                     {
                         SeriesId = 12345,
                         EpisodeIds = new List<int> { 1 },
                         Quality = new QualityModel(Quality.Bluray720p),
                         Languages = new List<Language> { Language.English },
                         SourceTitle = "series.title.s01e01",
                         Date = DateTime.UtcNow
                     };

            _series1 = Builder<Series>.CreateNew()
                                      .With(s => s.Id = 7)
                                      .Build();

            _series2 = Builder<Series>.CreateNew()
                                      .With(s => s.Id = 8)
                                      .Build();
        }

        [Test]
        public void should_be_able_to_write_to_database()
        {
            Subject.Insert(_blocklist);
            Subject.All().Should().HaveCount(1);
        }

        [Test]
        public void should_should_have_episode_ids()
        {
            Subject.Insert(_blocklist);

            Subject.All().First().EpisodeIds.Should().Contain(_blocklist.EpisodeIds);
        }

        [Test]
        public void should_check_for_blocklisted_title_case_insensative()
        {
            Subject.Insert(_blocklist);

            Subject.BlocklistedByTitle(_blocklist.SeriesId, _blocklist.SourceTitle.ToUpperInvariant()).Should().HaveCount(1);
        }

        [TestCase("series.title")]
        [TestCase("SERIES.TITLE.S01E01")]
        [TestCase("series_title")]
        [TestCase("series%title")]
        [TestCase("100%")]
        [TestCase("_")]
        [TestCase("%")]
        [TestCase("")]
        [TestCase("\u00c4rger")]
        [TestCase("\u00e4rger")]
        [TestCase("STRASSE")]
        [TestCase("s01e01.720p")]
        [TestCase("abc\\def")]
        [TestCase("ABCDEF0123")]
        public void in_memory_match_should_equal_database_match(string search)
        {
            var titles = new[]
            {
                "series.title.s01e01", "Series_Title_S01E01", "Series Title 100% Uncut", "\u00e4rger.s01e01",
                "Stra\u00dfe.S01E01", "abc\\def", "a.b.c", ""
            };

            var hashes = new[] { "abcdef0123", "ABCDEF0123", "x_y", null, null, null, null, null };

            var items = titles.Select((t, i) => new Blocklist
            {
                SeriesId = _series1.Id,
                EpisodeIds = new List<int> { 1 },
                Quality = new QualityModel(Quality.Bluray720p),
                Languages = new List<Language> { Language.English },
                SourceTitle = t,
                TorrentInfoHash = hashes[i],
                Date = DateTime.UtcNow
            }).ToList();

            Db.InsertMany(items);

            var all = Subject.BlocklistedBySeries(_series1.Id);

            all.Where(b => BlocklistService.SqliteLikeContains(b.SourceTitle, search)).Select(b => b.Id)
                .Should().BeEquivalentTo(Subject.BlocklistedByTitle(_series1.Id, search).Select(b => b.Id));

            all.Where(b => BlocklistService.SqliteLikeContains(b.TorrentInfoHash, search)).Select(b => b.Id)
                .Should().BeEquivalentTo(Subject.BlocklistedByTorrentInfoHash(_series1.Id, search).Select(b => b.Id));
        }

        [Test]
        public void should_delete_blocklists_by_seriesId()
        {
            var blocklistItems = Builder<Blocklist>.CreateListOfSize(5)
                .TheFirst(1)
                .With(c => c.SeriesId = _series2.Id)
                .TheRest()
                .With(c => c.SeriesId = _series1.Id)
                .All()
                .With(c => c.Quality = new QualityModel())
                .With(c => c.Languages = new List<Language>())
                .With(c => c.EpisodeIds = new List<int> { 1 })
                .BuildListOfNew();

            Db.InsertMany(blocklistItems);

            Subject.DeleteForSeriesIds(new List<int> { _series1.Id });

            var removedSeriesBlocklists = Subject.BlocklistedBySeries(_series1.Id);
            var nonRemovedSeriesBlocklists = Subject.BlocklistedBySeries(_series2.Id);

            removedSeriesBlocklists.Should().HaveCount(0);
            nonRemovedSeriesBlocklists.Should().HaveCount(1);
        }
    }
}
