using Mahjong.Advisor;
using Xunit;

namespace Mahjong.Advisor.Tests;

/// <summary>
/// Vectors generated from an independent reference implementation and checked
/// by hand against known hands. If a change to Shanten breaks one of these,
/// the change is wrong.
/// </summary>
public class ShantenVectorTests
{
    public static IEnumerable<object[]> Vectors()
    {
        yield return new object[] { "123456789m123p11s", -1, 5, 8, -1 };
        yield return new object[] { "111222333444m55p", -1, 3, 11, -1 };
        yield return new object[] { "123456789m123p1s", 0, 6, 9, 0 };
        yield return new object[] { "123456789m12p11s", 0, 5, 8, 0 };
        yield return new object[] { "123456789m12p1s3s", 1, 6, 9, 1 };
        yield return new object[] { "1122334455667m", 0, 0, 11, 0 };
        yield return new object[] { "112233445566m7p", 0, 0, 11, 0 };
        yield return new object[] { "19m19p19s1234567z", 8, 6, 0, 0 };
        yield return new object[] { "119m19p19s123456z", 7, 5, 0, 0 };
        yield return new object[] { "11223344556677m", -1, -1, 11, -1 };
        yield return new object[] { "123m456p789s11122z", -1, 4, 8, -1 };
        yield return new object[] { "1113579m1357p24s", 3, 5, 9, 3 };
        yield return new object[] { "2345678m23456p11s", 0, 5, 11, 0 };
        yield return new object[] { "1122m3344p5566s7z", 3, 0, 10, 0 };
        yield return new object[] { "111m123456789p11s", -1, 4, 8, -1 };
        yield return new object[] { "456m456p456s4477z", 0, 4, 10, 0 };
        yield return new object[] { "13579m13579p1357s", 4, 6, 8, 4 };
        yield return new object[] { "1234m1234p1234s1z", 2, 6, 9, 2 };
        yield return new object[] { "111122223333m44p", -1, 5, 11, -1 };
        yield return new object[] { "1112345678999m", 0, 4, 10, 0 };
        yield return new object[] { "15799m46p1577s37z", 4, 4, 7, 4 };
        yield return new object[] { "12478m18p3s23357z", 5, 5, 6, 5 };
        yield return new object[] { "2249m8p1347s2355z", 5, 4, 7, 4 };
        yield return new object[] { "66m47p227889s134z", 4, 3, 9, 3 };
        yield return new object[] { "3368m16789p578s7z", 3, 5, 10, 3 };
        yield return new object[] { "5m23445p258s2356z", 5, 5, 9, 5 };
        yield return new object[] { "15678m138p345s15z", 3, 6, 9, 3 };
        yield return new object[] { "455m345679s1247z", 4, 5, 8, 4 };
        yield return new object[] { "13569p44s156777z", 3, 4, 6, 3 };
        yield return new object[] { "689m344489p13s13z", 3, 5, 8, 3 };
        yield return new object[] { "369m27p236s12556z", 6, 5, 7, 5 };
        yield return new object[] { "3448m12799p25s15z", 5, 4, 8, 4 };
        yield return new object[] { "5556899m39p229s4z", 3, 3, 8, 3 };
        yield return new object[] { "1279m3577p346s34z", 3, 5, 9, 3 };
        yield return new object[] { "33588m1699p29s66z", 4, 2, 8, 2 };
        yield return new object[] { "1278m24p156s1244z", 3, 5, 7, 3 };
        yield return new object[] { "459m3569p269s247z", 6, 6, 7, 6 };
        yield return new object[] { "36789m348p449s24z", 4, 5, 9, 4 };
        yield return new object[] { "458m11344p34s227z", 3, 3, 9, 3 };
        yield return new object[] { "2578m467p22566s2z", 4, 4, 12, 4 };
        yield return new object[] { "3m1125569p3568s4z", 4, 4, 9, 4 };
        yield return new object[] { "2458m5p23468s555z", 2, 5, 11, 2 };
        yield return new object[] { "288m5679p169s112z", 4, 4, 7, 4 };
        yield return new object[] { "4579m58p68s66677z", 2, 4, 9, 2 };
        yield return new object[] { "12233567m468p49s", 2, 4, 11, 2 };
        yield return new object[] { "3479m2p122679s46z", 4, 5, 8, 4 };
        yield return new object[] { "14m33p567899s147z", 4, 4, 7, 4 };
        yield return new object[] { "13668m7p19s11366z", 4, 3, 6, 3 };
        yield return new object[] { "68m2568p3569s145z", 5, 6, 9, 5 };
        yield return new object[] { "56m2456p19s12345z", 5, 6, 6, 5 };
        yield return new object[] { "347m168p12336s37z", 4, 5, 9, 4 };
        yield return new object[] { "456m1156p3s33467z", 3, 4, 7, 3 };
        yield return new object[] { "4589m148p4679s47z", 4, 6, 8, 4 };
        yield return new object[] { "1368m11467p49s37z", 4, 5, 7, 4 };
        yield return new object[] { "4589m11p2468s347z", 3, 5, 7, 3 };
        yield return new object[] { "6889p12357s1147z", 2, 4, 7, 2 };
        yield return new object[] { "2234557m467p59s4z", 3, 4, 11, 3 };
        yield return new object[] { "13588m348p23688s", 3, 4, 12, 3 };
        yield return new object[] { "1245m258p334s124z", 5, 5, 9, 5 };
        yield return new object[] { "36689m27p1268s11z", 3, 4, 9, 3 };
        yield return new object[] { "27889m26p256s135z", 5, 5, 9, 5 };
        yield return new object[] { "268m788p3458s167z", 4, 5, 10, 4 };
        yield return new object[] { "2455m148p12339s5z", 4, 4, 9, 4 };
        yield return new object[] { "17m13488p14579s3z", 4, 5, 8, 4 };
        yield return new object[] { "89m1247p599s1336z", 4, 4, 6, 4 };
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void MatchesReference(string notation, int standard, int chiitoi, int kokushi, int best)
    {
        var hand = Tiles.Parse(notation);
        Assert.InRange(Tiles.Total(hand), 13, 14);
        Assert.Equal(standard, Shanten.Standard(hand));
        Assert.Equal(chiitoi, Shanten.SevenPairs(hand));
        Assert.Equal(kokushi, Shanten.ThirteenOrphans(hand));
        Assert.Equal(best, Shanten.Calculate(hand));
    }

    [Fact]
    public void SevenPairsWinsWhenTheStandardShapeIsFarBehind()
    {
        // Six pairs of terminals and honours: seven-pairs says tenpai while the
        // four-melds-and-a-pair reading is three away. Calculate must take the best.
        var hand = Tiles.Parse("1199m1199p1199s1z");

        Assert.Equal(0, Shanten.SevenPairs(hand));
        Assert.Equal(3, Shanten.Standard(hand));
        Assert.Equal(0, Shanten.Calculate(hand, calledMelds: 0));
    }

    [Fact]
    public void CalledMeldsCountTowardTheHand()
    {
        // Eleven concealed tiles is the legal shape once one meld is called.
        // 123m 456m 789m + the called meld is four sets, so 12p only has to pair.
        var hand = Tiles.Parse("123456789m12p");
        Assert.Equal(11, Tiles.Total(hand));

        Assert.Equal(0, Shanten.Calculate(hand, calledMelds: 1));
        Assert.Equal(1, Shanten.Standard(hand, calledMelds: 0));
    }

    [Fact]
    public void ParseRoundTrips()
    {
        const string notation = "123456789m123p11s";
        Assert.Equal(notation, Tiles.Format(Tiles.Parse(notation)));
    }
}
