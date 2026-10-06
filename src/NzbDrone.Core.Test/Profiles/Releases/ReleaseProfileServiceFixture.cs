using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
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
    }
}
