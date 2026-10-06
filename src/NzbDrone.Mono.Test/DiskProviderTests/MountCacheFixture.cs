using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Mono.Disk;
using NzbDrone.Test.Common;

namespace NzbDrone.Mono.Test.DiskProviderTests
{
    [TestFixture]
    [Platform(Exclude = "Win")]
    public class MountCacheFixture : TestBase<DiskProvider>
    {
        private long _freeSpace;

        [SetUp]
        public void Setup()
        {
            _freeSpace = 1000;

            Mocker.GetMock<ISymbolicLinkResolver>()
                  .Setup(v => v.GetCompleteRealPath(It.IsAny<string>()))
                  .Returns<string>(s => s);

            Mocker.GetMock<IProcMountProvider>()
                  .Setup(v => v.GetMounts())
                  .Returns(() => new List<IMount> { GivenMount("/"), GivenMount("/mnt/media") });
        }

        private IMount GivenMount(string root)
        {
            var mount = new Mock<IMount>();
            mount.SetupGet(m => m.RootDirectory).Returns(root);
            mount.SetupGet(m => m.AvailableFreeSpace).Returns(() => _freeSpace);

            return mount.Object;
        }

        [Test]
        public void should_read_mounts_once_within_the_cache_duration_and_free_space_live()
        {
            Subject.GetAvailableSpace("/mnt/media/tv").Should().Be(1000);

            _freeSpace = 500;

            Subject.GetAvailableSpace("/mnt/media/tv").Should().Be(500);

            Mocker.GetMock<IProcMountProvider>().Verify(v => v.GetMounts(), Times.Once());
        }

        [Test]
        public void should_reread_mounts_after_the_cache_expired()
        {
            var subject = Mocker.Resolve<ExpiringMountsDiskProvider>();

            subject.GetMount("/mnt/media/tv").RootDirectory.Should().Be("/mnt/media");

            Mocker.GetMock<IProcMountProvider>()
                  .Setup(v => v.GetMounts())
                  .Returns(() => new List<IMount> { GivenMount("/") });

            subject.GetMount("/mnt/media/tv").RootDirectory.Should().Be("/");

            Mocker.GetMock<IProcMountProvider>().Verify(v => v.GetMounts(), Times.Exactly(2));
        }

        public class ExpiringMountsDiskProvider : DiskProvider
        {
            public ExpiringMountsDiskProvider(IProcMountProvider procMountProvider, ISymbolicLinkResolver symLinkResolver, ICreateRefLink createRefLink, NLog.Logger logger)
                : base(procMountProvider, symLinkResolver, createRefLink, logger)
            {
            }

            protected override TimeSpan MountCacheLifetime => TimeSpan.Zero;
        }
    }
}
