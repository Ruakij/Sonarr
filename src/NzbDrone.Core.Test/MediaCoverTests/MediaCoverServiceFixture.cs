using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Test.MediaCoverTests
{
    [TestFixture]
    public class MediaCoverServiceFixture : CoreTest<MediaCoverService>
    {
        private Series _series;

        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<IAppFolderInfo>(new AppFolderInfo(Mocker.Resolve<IStartupContext>()));

            _series = Builder<Series>.CreateNew()
                .With(v => v.Id = 2)
                .With(v => v.Images = new List<MediaCover.MediaCover> { new MediaCover.MediaCover(MediaCoverTypes.Poster, "") })
                .Build();
        }

        [Test]
        public void should_convert_cover_urls_to_local()
        {
            var covers = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover { CoverType = MediaCoverTypes.Banner }
                };

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileGetLastWrite(It.IsAny<string>()))
                  .Returns(new DateTime(1234));

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Subject.ConvertToLocalUrls(12, covers);

            covers.Single().Url.Should().Be("/MediaCover/12/banner.jpg?lastWrite=1234");
        }

        [Test]
        public void should_convert_media_urls_to_local_without_time_if_file_doesnt_exist()
        {
            var covers = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover { CoverType = MediaCoverTypes.Banner }
                };

            Subject.ConvertToLocalUrls(12, covers);

            covers.Single().Url.Should().Be("/MediaCover/12/banner.jpg");
        }

        private string ConvertPoster()
        {
            var covers = new List<MediaCover.MediaCover> { new MediaCover.MediaCover { CoverType = MediaCoverTypes.Poster } };

            Subject.ConvertToLocalUrls(_series.Id, covers);

            return covers.Single().Url;
        }

        private void GivenPosterWritten(long? ticks)
        {
            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>())).Returns(ticks.HasValue);
            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileGetLastWrite(It.IsAny<string>())).Returns(new DateTime(ticks ?? 0));
        }

        private void GivenPosterExists(bool exists)
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(exists);
        }

        [Test]
        public void should_check_the_cover_file_once()
        {
            GivenPosterWritten(1234);

            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg?lastWrite=1234");
            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg?lastWrite=1234");

            Mocker.GetMock<IDiskProvider>().Verify(c => c.FileExists(It.IsAny<string>()), Times.Once());
        }

        [Test]
        public void should_return_the_last_write_of_a_downloaded_cover()
        {
            GivenPosterWritten(null);
            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg");

            GivenPosterExists(false);
            GivenPosterWritten(1234);
            Subject.HandleAsync(new SeriesUpdatedEvent(_series));

            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg?lastWrite=1234");
        }

        [Test]
        public void should_refresh_the_last_write_when_the_series_is_updated()
        {
            GivenPosterWritten(1234);
            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg?lastWrite=1234");

            GivenPosterExists(true);
            GivenPosterWritten(5678);
            Subject.HandleAsync(new SeriesUpdatedEvent(_series));

            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg?lastWrite=5678");
        }

        [Test]
        public void should_forget_the_last_write_when_the_series_is_deleted()
        {
            GivenPosterWritten(1234);
            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg?lastWrite=1234");

            Subject.HandleAsync(new SeriesDeletedEvent(new List<Series> { _series }, false, false));
            GivenPosterWritten(null);

            ConvertPoster().Should().Be("/MediaCover/2/poster.jpg");
        }

        [Test]
        public void should_resize_covers_if_main_downloaded()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(false);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Subject.HandleAsync(new SeriesUpdatedEvent(_series));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }

        [Test]
        public void should_resize_covers_if_missing()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(false);

            Subject.HandleAsync(new SeriesUpdatedEvent(_series));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }

        [Test]
        public void should_not_resize_covers_if_exists()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.GetFileSize(It.IsAny<string>()))
                  .Returns(1000);

            Subject.HandleAsync(new SeriesUpdatedEvent(_series));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_resize_covers_if_existing_is_empty()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.GetFileSize(It.IsAny<string>()))
                  .Returns(0);

            Subject.HandleAsync(new SeriesUpdatedEvent(_series));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }

        [Test]
        public void should_log_error_if_resize_failed()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(false);

            Mocker.GetMock<IImageResizer>()
                  .Setup(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                  .Throws<ApplicationException>();

            Subject.HandleAsync(new SeriesUpdatedEvent(_series));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }
    }
}
