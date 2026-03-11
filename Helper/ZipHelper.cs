using Ionic.Zip;
using System.IO;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public static class ZipHelper
    {
        public static void ExtractFile(string zipFile, string outputDir, string password)
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);

            using (var zip = new ZipFile(zipFile))
            {
                zip.Password = password;
                zip.ExtractAll(outputDir);
            }
        }
    }
}