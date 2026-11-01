using ApexMapper.Core.Sensors;
using Xunit;

namespace ApexMapper.Core.Tests.Sensors;

public class SupportIssueTests
{
    [Fact]
    public void The_link_fills_every_field_the_app_knows_with_the_form_s_own_labels()
    {
        var url = SupportIssue.Url("Apex Pro TKL Gen 3", 0x1642, "1.2.0", SensorSupport.Unverified, SensorCheckResult.Passed, "0.5.1-alpha");

        Assert.Equal(
            "https://github.com/lavindeep/apex-analog-mapper/issues/new?template=keyboard_support.yml" +
            "&model=Apex%20Pro%20TKL%20Gen%203&hardware_id=HID%5CVID_1038%26PID_1642&firmware=1.2.0" +
            "&card=Not%20tested%20yet&check=It%20passed&version=0.5.1-alpha",
            url);
    }

    [Fact]
    public void A_field_the_app_does_not_know_or_the_form_has_no_option_for_is_left_out()
    {
        var url = SupportIssue.Url("Apex Pro TKL", 0x1614, null, SensorSupport.Verified, null, "0.5.0");

        Assert.DoesNotContain("firmware=", url);
        Assert.DoesNotContain("card=", url);
        Assert.DoesNotContain("check=", url);
        Assert.EndsWith("&version=0.5.0", url);
        Assert.Contains("&card=Not%20supported", SupportIssue.Url(null, null, null, SensorSupport.Unsupported, SensorCheckResult.NotRun, null));
        Assert.Contains("&check=I%20didn%27t%20run%20it", SupportIssue.Url(null, null, null, null, SensorCheckResult.NotRun, null));
    }
}
