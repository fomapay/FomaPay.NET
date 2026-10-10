using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FomaPay.Pos
{
    /// <summary>Alignement d'une ligne sur le ticket.</summary>
    public enum Alignment
    {
        /// <summary>À gauche.</summary>
        Left = 0,

        /// <summary>Centré.</summary>
        Center = 1,

        /// <summary>À droite.</summary>
        Right = 2,
    }

    /// <summary>Largeur du papier de l'imprimante thermique.</summary>
    public enum PaperWidth
    {
        /// <summary>Papier 58 mm : 32 caractères par ligne.</summary>
        Mm58 = 32,

        /// <summary>Papier 80 mm : 48 caractères par ligne.</summary>
        Mm80 = 48,
    }

    /// <summary>
    /// Construit un ticket au format ESC/POS, compris par la quasi-totalité des imprimantes thermiques
    /// (Epson, Xprinter, Bixolon, Sunmi…). Le résultat est un tableau d'octets à envoyer tel quel à l'imprimante.
    /// </summary>
    /// <remarks>
    /// Les accents sont retirés (« Reçu » devient « Recu ») : la page de codes varie d'une imprimante à l'autre,
    /// l'ASCII s'imprime correctement partout.
    /// </remarks>
    public sealed class EscPosBuilder
    {
        private const byte Esc = 0x1B;
        private const byte Gs = 0x1D;

        private readonly MemoryStream _buffer = new MemoryStream();

        /// <param name="paperWidth">Largeur du papier (58 mm par défaut, la plus répandue en caisse mobile).</param>
        public EscPosBuilder(PaperWidth paperWidth = PaperWidth.Mm58)
        {
            Columns = (int)paperWidth;
            Raw(Esc, (byte)'@'); // réinitialise l'imprimante
        }

        /// <summary>Nombre de caractères par ligne en taille normale.</summary>
        public int Columns { get; }

        /// <summary>Aligne les lignes suivantes.</summary>
        public EscPosBuilder Align(Alignment alignment) => Raw(Esc, (byte)'a', (byte)alignment);

        /// <summary>Active ou coupe le gras.</summary>
        public EscPosBuilder Bold(bool on = true) => Raw(Esc, (byte)'E', (byte)(on ? 1 : 0));

        /// <summary>Double la taille des caractères (titres, montant) ou revient à la taille normale.</summary>
        public EscPosBuilder DoubleSize(bool on = true) => Raw(Gs, (byte)'!', (byte)(on ? 0x11 : 0x00));

        /// <summary>Écrit une ligne de texte.</summary>
        public EscPosBuilder Line(string text = "")
        {
            var bytes = Encoding.ASCII.GetBytes(ToAscii(text));
            _buffer.Write(bytes, 0, bytes.Length);
            _buffer.WriteByte(0x0A);
            return this;
        }

        /// <summary>Écrit un libellé à gauche et une valeur à droite sur la même ligne.</summary>
        public EscPosBuilder Row(string label, string value)
        {
            label = ToAscii(label);
            value = ToAscii(value);
            var gap = Columns - label.Length - value.Length;
            if (gap < 1)
            {
                // Trop long pour une ligne : la valeur passe à la ligne suivante, à droite.
                return Line(label).Line(value.PadLeft(Columns));
            }
            return Line(label + new string(' ', gap) + value);
        }

        /// <summary>Trace une ligne de séparation.</summary>
        public EscPosBuilder Separator(char character = '-') => Line(new string(character, Columns));

        /// <summary>Avance le papier.</summary>
        public EscPosBuilder Feed(int lines = 1) => Raw(Esc, (byte)'d', (byte)Math.Max(0, Math.Min(lines, 255)));

        /// <summary>Imprime un QR code (ex. la référence du paiement, à scanner pour le retrouver).</summary>
        /// <param name="content">Texte encodé dans le QR code.</param>
        /// <param name="moduleSize">Taille d'un module, de 1 à 16 (6 par défaut).</param>
        public EscPosBuilder QrCode(string content, int moduleSize = 6)
        {
            var data = Encoding.UTF8.GetBytes(content ?? "");
            var length = data.Length + 3;
            Raw(Gs, (byte)'(', (byte)'k', 4, 0, 0x31, 0x41, 0x32, 0);                                   // modèle 2
            Raw(Gs, (byte)'(', (byte)'k', 3, 0, 0x31, 0x43, (byte)Math.Max(1, Math.Min(moduleSize, 16))); // taille
            Raw(Gs, (byte)'(', (byte)'k', 3, 0, 0x31, 0x45, 0x31);                                      // correction M
            Raw(Gs, (byte)'(', (byte)'k', (byte)(length & 0xFF), (byte)(length >> 8), 0x31, 0x50, 0x30); // données
            _buffer.Write(data, 0, data.Length);
            return Raw(Gs, (byte)'(', (byte)'k', 3, 0, 0x31, 0x51, 0x30);                               // impression
        }

        /// <summary>Coupe le papier (ignoré par les imprimantes sans massicot).</summary>
        public EscPosBuilder Cut() => Feed(3).Raw(Gs, (byte)'V', 0x42, 0);

        /// <summary>Ouvre le tiroir-caisse relié à l'imprimante.</summary>
        public EscPosBuilder OpenCashDrawer() => Raw(Esc, (byte)'p', 0, 25, 250);

        /// <summary>Ajoute des octets bruts (commande propre à un modèle d'imprimante).</summary>
        public EscPosBuilder Raw(params byte[] bytes)
        {
            _buffer.Write(bytes, 0, bytes.Length);
            return this;
        }

        /// <summary>Les octets à envoyer à l'imprimante.</summary>
        public byte[] Build() => _buffer.ToArray();

        /// <summary>Retire les accents et remplace tout caractère hors ASCII par « ? ».</summary>
        internal static string ToAscii(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var result = new StringBuilder(text!.Length);
            foreach (var c in text.Replace("œ", "oe").Replace("Œ", "OE").Replace('’', '\'').Replace('…', '.').Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }
                result.Append(c == ' ' || c == ' ' ? ' ' : c >= 0x20 && c < 0x7F ? c : '?');
            }
            return result.ToString();
        }
    }
}
