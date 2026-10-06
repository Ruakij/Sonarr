using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.FileList;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests
{
    public class IndexerServiceFixture : DbTest<IndexerFactory, IndexerDefinition>
    {
        private List<IIndexer> _indexers;

        [SetUp]
        public void Setup()
        {
            _indexers = new List<IIndexer>();

            _indexers.Add(Mocker.Resolve<Newznab>());
            _indexers.Add(Mocker.Resolve<FileList>());

            Mocker.SetConstant<IEnumerable<IIndexer>>(_indexers);
        }

        [Test]
        public void should_remove_missing_indexers_on_startup()
        {
            var repo = Mocker.Resolve<IndexerRepository>();

            Mocker.SetConstant<IIndexerRepository>(repo);

            var existingIndexers = Builder<IndexerDefinition>.CreateNew().BuildNew();
            existingIndexers.ConfigContract = nameof(NewznabSettings);

            repo.Insert(existingIndexers);

            Subject.Handle(new ApplicationStartedEvent());

            AllStoredModels.Should().NotContain(c => c.Id == existingIndexers.Id);

            ExceptionVerification.ExpectedWarns(1);
        }

        private IndexerDefinition GivenIndexer(string name)
        {
            Mocker.SetConstant<IIndexerRepository>(Mocker.Resolve<IndexerRepository>());

            var definition = Builder<IndexerDefinition>.CreateNew()
                .With(d => d.Id = 0)
                .With(d => d.Name = name)
                .With(d => d.Implementation = nameof(Newznab))
                .With(d => d.ConfigContract = nameof(NewznabSettings))
                .With(d => d.Settings = new NewznabSettings())
                .BuildNew();

            return Subject.Create(definition);
        }

        [Test]
        public void should_serve_repeated_lookups_from_the_cache()
        {
            var indexer = GivenIndexer("Indexer1");

            Subject.Get(indexer.Id).Should().BeSameAs(Subject.Find(indexer.Id));
        }

        [Test]
        public void should_return_the_updated_definition_after_an_update()
        {
            var indexer = GivenIndexer("Indexer1");
            Subject.Get(indexer.Id).Name.Should().Be("Indexer1");

            var updated = Builder<IndexerDefinition>.CreateNew()
                .With(d => d.Id = indexer.Id)
                .With(d => d.Name = "Renamed")
                .With(d => d.Implementation = nameof(Newznab))
                .With(d => d.ConfigContract = nameof(NewznabSettings))
                .With(d => d.Settings = new NewznabSettings())
                .Build();

            Subject.Update(updated);

            Subject.Get(indexer.Id).Name.Should().Be("Renamed");
        }

        [Test]
        public void should_return_bulk_updated_definitions()
        {
            var indexer = GivenIndexer("Indexer1");
            Subject.Find(indexer.Id).Tags.Should().BeEmpty();

            var fresh = Subject.Get(new[] { indexer.Id }).ToList();
            fresh.Single().Tags = new HashSet<int> { 5 };
            Subject.Update(fresh);

            Subject.Find(indexer.Id).Tags.Should().BeEquivalentTo(new[] { 5 });
        }

        [Test]
        public void should_find_an_indexer_added_after_the_cache_was_loaded()
        {
            Mocker.SetConstant<IIndexerRepository>(Mocker.Resolve<IndexerRepository>());
            Subject.Find(1).Should().BeNull();

            var indexer = GivenIndexer("Indexer1");

            Subject.Find(indexer.Id).Should().NotBeNull();
            Subject.Exists(indexer.Id).Should().BeTrue();
        }

        [Test]
        public void should_not_find_a_deleted_indexer()
        {
            var indexer = GivenIndexer("Indexer1");
            Subject.Find(indexer.Id).Should().NotBeNull();

            Subject.Delete(indexer.Id);

            Subject.Find(indexer.Id).Should().BeNull();
            Subject.Exists(indexer.Id).Should().BeFalse();
        }
    }
}
