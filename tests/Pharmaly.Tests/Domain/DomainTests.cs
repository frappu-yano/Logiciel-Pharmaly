using Pharmaly.Domain;

namespace Pharmaly.Tests.Domain;

public sealed class DomainTests
{
    [Theory]
    [InlineData("Crème  Hydratante", "creme hydratante")]
    [InlineData("  ÉCRAN solaire SPF50+ ", "ecran solaire spf50+")]
    [InlineData("Anti-âge", "anti-age")]
    public void La_cle_de_recherche_ignore_accents_casse_et_espaces(string text, string expected) =>
        Assert.Equal(expected, TextNormalizer.ToSearchKey(text));

    [Fact]
    public void Un_code_EAN13_genere_est_valide() =>
        Assert.True(Ean13.IsValid(Ean13.WithCheckDigit("200000000014")));

    [Theory]
    [InlineData("4006381333931", true)]
    [InlineData("4006381333932", false)]
    [InlineData("400638133393", false)]
    [InlineData("40063813339A1", false)]
    public void La_cle_de_controle_EAN13_est_verifiee(string code, bool expected) =>
        Assert.Equal(expected, Ean13.IsValid(code));

    [Theory]
    [InlineData("0", "0,00 DA")]
    [InlineData("1250.5", "1 250,50 DA")]
    [InlineData("1234567.89", "1 234 567,89 DA")]
    public void Un_montant_s_affiche_en_dinars(string amount, string expected) =>
        Assert.Equal(expected, Money.ToDisplay(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Les_montants_ne_subissent_aucune_erreur_d_arrondi()
    {
        // 0,10 + 0,20 vaut exactement 0,30 en decimal, ce qui est faux en virgule flottante.
        var total = Enumerable.Repeat(0.10m, 3).Sum();
        Assert.Equal(0.30m, total);
    }
}
