using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles.Releases
{
    [TestFixture]
    public class ReleaseProfileServiceFixture : DbTest<ReleaseProfileService, ReleaseProfile>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<IRestrictionRepository>(Mocker.Resolve<ReleaseProfileRepository>());
        }

        private ReleaseProfile GivenProfile(string required)
        {
            return Subject.Add(new ReleaseProfile { Name = required, Required = new List<string> { required } });
        }

        [Test]
        public void should_return_added_profile_after_profiles_were_loaded()
        {
            Subject.All().Should().BeEmpty();

            GivenProfile("x265");

            Subject.All().Should().ContainSingle(p => p.Name == "x265");
        }

        [Test]
        public void should_return_updated_profile()
        {
            var profile = GivenProfile("x265");
            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(1);

            Subject.Update(new ReleaseProfile { Id = profile.Id, Name = profile.Name, Enabled = false, Required = profile.Required });

            Subject.EnabledForTags(new HashSet<int>(), 0).Should().BeEmpty();
            Subject.All().Single().Enabled.Should().BeFalse();
        }

        [Test]
        public void should_not_return_deleted_profile()
        {
            var profile = GivenProfile("x265");
            GivenProfile("h264");
            Subject.All().Should().HaveCount(2);

            Subject.Delete(profile.Id);

            Subject.All().Should().ContainSingle(p => p.Name == "h264");
        }

        [Test]
        public void should_not_keep_profiles_loaded_while_a_write_cleared_them()
        {
            var repo = new Mock<IRestrictionRepository>();
            var stored = new List<ReleaseProfile> { new ReleaseProfile { Id = 1, Tags = new HashSet<int>() } };
            Task delete = null;

            repo.Setup(s => s.All()).Returns(() =>
            {
                var loaded = stored.ToList();

                if (delete == null)
                {
                    // The write waits until the profiles read before it are stored, so it clears them afterwards
                    delete = Task.Run(() => Subject.Delete(1));
                    delete.Wait(TimeSpan.FromMilliseconds(200));
                }

                return loaded;
            });
            repo.Setup(s => s.Delete(1)).Callback(() => stored.Clear());

            Mocker.SetConstant(repo.Object);

            Subject.All().Should().HaveCount(1);
            delete.Wait();

            Subject.All().Should().BeEmpty();
        }
    }
}
