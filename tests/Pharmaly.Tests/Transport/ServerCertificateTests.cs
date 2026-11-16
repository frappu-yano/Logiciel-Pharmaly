using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services;
using Pharmaly.Server.Transport;

namespace Pharmaly.Tests.Transport;

/// <summary>Le certificat du serveur et l'empreinte que l'installateur compare.</summary>
public sealed class ServerCertificateTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("pharmaly-certificat-").FullName;

    private string CertificateFile => Path.Combine(directory, "certificat-serveur.pfx");

    [Fact]
    public void Le_certificat_est_cree_une_fois_puis_reutilise_a_chaque_demarrage()
    {
        using var first = ServerCertificateStore.LoadOrCreate(CertificateFile);
        var written = File.ReadAllBytes(CertificateFile);

        using var afterRestart = ServerCertificateStore.LoadOrCreate(CertificateFile);

        Assert.Equal(CertificateFingerprint.Of(first), CertificateFingerprint.Of(afterRestart));
        Assert.Equal(written, File.ReadAllBytes(CertificateFile));
        Assert.True(afterRestart.HasPrivateKey);
        Assert.True(afterRestart.NotAfter > DateTime.Now.AddYears(25));
        Assert.Equal([Path.GetFileName(CertificateFile)], Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public void Deux_serveurs_n_ont_jamais_le_meme_certificat()
    {
        using var first = ServerCertificateStore.LoadOrCreate(CertificateFile);
        using var other = ServerCertificateStore.LoadOrCreate(Path.Combine(directory, "autre.pfx"));

        Assert.NotEqual(CertificateFingerprint.Of(first), CertificateFingerprint.Of(other));
    }

    [Fact]
    public void Le_fichier_de_la_cle_n_est_pas_lisible_par_les_autres_utilisateurs()
    {
        ServerCertificateStore.LoadOrCreate(CertificateFile).Dispose();

        if (OperatingSystem.IsWindows())
        {
            // Sous Windows le fichier est chiffré pour la machine : tel quel, ce n'est pas un certificat lisible.
            Assert.ThrowsAny<Exception>(() => X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(CertificateFile), null));
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CertificateFile));
        }
    }

    [Fact]
    public void Un_fichier_de_certificat_illisible_arrete_le_serveur_au_lieu_d_etre_remplace_en_silence()
    {
        byte[] damaged = [1, 2, 3, 4];
        File.WriteAllBytes(CertificateFile, damaged);

        var failure = Assert.Throws<InvalidOperationException>(() => ServerCertificateStore.LoadOrCreate(CertificateFile));

        Assert.Contains("illisible", failure.Message);
        Assert.Contains("--oublier-serveur", failure.Message);
        Assert.Equal(damaged, File.ReadAllBytes(CertificateFile));
    }

    [Fact]
    public void La_commande_empreinte_affiche_l_empreinte_du_certificat_en_groupes_lisibles()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transport:CertificateFile"] = CertificateFile })
            .Build();

        var shown = EncryptedTransport.DescribeFingerprint(configuration);
        var shownAgain = EncryptedTransport.DescribeFingerprint(configuration);

        using var certificate = ServerCertificateStore.LoadOrCreate(CertificateFile);
        var fingerprint = CertificateFingerprint.Of(certificate);
        var lines = shown.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(shown, shownAgain);
        Assert.Equal($"EMPREINTE={fingerprint}", lines[^1]);
        Assert.Equal(CertificateFingerprint.Format(fingerprint), string.Join('\n', lines[3], lines[4]));
        Assert.Matches("^([0-9A-F]{4} ){7}[0-9A-F]{4}$", lines[3]);
    }

    [Fact]
    public void L_empreinte_est_le_sha_256_du_certificat()
    {
        using var certificate = ServerCertificateStore.LoadOrCreate(CertificateFile);

        Assert.Equal(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), CertificateFingerprint.Of(certificate));
    }

    [Fact]
    public void L_empreinte_approuvee_par_le_poste_est_conservee_et_lisible_par_lui_seul()
    {
        var path = Path.Combine(directory, "poste", "serveur.empreinte");
        var fingerprint = new string('A', 64);

        Assert.Null(new ServerPinStore(path).Load());
        new ServerPinStore(path).Save(fingerprint);

        Assert.Equal(fingerprint, new ServerPinStore(path).Load());
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(fingerprint, File.ReadAllText(path));
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        new ServerPinStore(path).Clear();
        Assert.Null(new ServerPinStore(path).Load());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

public sealed class CertificateFingerprintTests
{
    private const string Fingerprint = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    [Fact]
    public void L_empreinte_s_affiche_en_groupes_de_quatre_caracteres_sur_deux_lignes() =>
        Assert.Equal(
            "9F86 D081 884C 7D65 9A2F EAA0 C55A D015\nA3BF 4F1B 2B0B 822C D15D 6C15 B0F0 0A08",
            CertificateFingerprint.Format(Fingerprint));

    [Theory]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("9F86 D081 884C 7D65 9A2F EAA0 C55A D015\nA3BF 4F1B 2B0B 822C D15D 6C15 B0F0 0A08")]
    [InlineData("  9f:86:d0:81:88:4c:7d:65:9a:2f:ea:a0:c5:5a:d0:15:a3:bf:4f:1b:2b:0b:82:2c:d1:5d:6c:15:b0:f0:0a:08\r\n")]
    [InlineData("9F86-D081-884C-7D65-9A2F-EAA0-C55A-D015-A3BF-4F1B-2B0B-822C-D15D-6C15-B0F0-0A08")]
    public void La_lecture_d_une_empreinte_ignore_la_casse_et_les_separateurs(string written)
    {
        Assert.Equal(Fingerprint, CertificateFingerprint.Parse(written));
        Assert.True(CertificateFingerprint.Same(written, Fingerprint));
        Assert.Equal(Fingerprint, CertificateFingerprint.Parse(CertificateFingerprint.Format(written)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9F86D081")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A0")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A0800")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A0G")]
    [InlineData("９F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    public void Un_texte_qui_n_est_pas_une_empreinte_complete_est_rejete(string? written)
    {
        Assert.Null(CertificateFingerprint.Parse(written));
        Assert.False(CertificateFingerprint.Same(written, written));
        Assert.False(CertificateFingerprint.Same(written, Fingerprint));
    }

    [Fact]
    public void Deux_empreintes_qui_different_d_un_seul_caractere_ne_sont_pas_les_memes() =>
        Assert.False(CertificateFingerprint.Same(Fingerprint, Fingerprint[..^1] + "9"));
}
