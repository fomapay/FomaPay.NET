using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FomaPay.Pos
{
    /// <summary>Envoie un ticket ESC/POS à une imprimante thermique.</summary>
    public static class ReceiptPrinter
    {
        /// <summary>Port « RAW » des imprimantes réseau (Ethernet / Wi-Fi).</summary>
        public const int DefaultPort = 9100;

        /// <summary>Imprime sur une imprimante réseau.</summary>
        /// <param name="host">Adresse IP ou nom de l'imprimante.</param>
        /// <param name="data">Ticket produit par <see cref="Receipt.ForPayment"/> ou <see cref="EscPosBuilder.Build"/>.</param>
        /// <param name="port">Port TCP (9100 par défaut).</param>
        /// <param name="timeout">Délai de connexion (5 s par défaut).</param>
        /// <param name="cancellationToken">Annulation.</param>
        /// <exception cref="FomaPayException"><c>ERROR_PRINTER_UNREACHABLE</c> si l'imprimante ne répond pas.</exception>
        public static async Task PrintAsync(
            string host, byte[] data, int port = DefaultPort, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    var limit = Task.Delay(timeout ?? TimeSpan.FromSeconds(5), cancellationToken);
                    if (await Task.WhenAny(connect, limit).ConfigureAwait(false) != connect)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("délai de connexion dépassé");
                    }
                    await connect.ConfigureAwait(false);
                    await PrintAsync(client.GetStream(), data, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is SocketException || e is IOException || e is TimeoutException)
            {
                throw new FomaPayException(
                    "ERROR_PRINTER_UNREACHABLE", "Impossible d'imprimer sur " + host + ":" + port + " : " + e.Message, 0, null, e);
            }
        }

        /// <summary>
        /// Imprime sur un flux déjà ouvert : port série ou USB (<c>new FileStream("COM3", …)</c>, <c>/dev/usb/lp0</c>),
        /// Bluetooth, ou tout autre transport.
        /// </summary>
        public static async Task PrintAsync(Stream output, byte[] data, CancellationToken cancellationToken = default)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            await output.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
