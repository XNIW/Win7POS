using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    public static class HistoricalWebImportPolicy
    {
        public const string WebParserVersion="getnet-ooxml-web-v1";
        public static string PrincipalFingerprint(PosTrustedDeviceSession trusted)
        {
            if(trusted==null||!HistoricalWebImportResponseMapper.Id(trusted.ShopId)||!HistoricalWebImportResponseMapper.Id(trusted.ShopDeviceId)||
                !HistoricalWebImportResponseMapper.Id(trusted.StaffId)||trusted.StaffCredentialVersion<1)throw new ArgumentException("history_web_principal_invalid");
            // The current session still authorizes every HTTP call. It is not
            // the durable owner identity across a legitimate v6 online re-login.
            using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",new[]{
                "history-web-principal-v6",trusted.ShopId,trusted.ShopDeviceId,trusted.StaffId,trusted.StaffCredentialVersion.ToString(CultureInfo.InvariantCulture)})))).Replace("-","").ToLowerInvariant();
        }
        public static IReadOnlyList<HistoricalQualifiedEvidence> ApplicableEvidence(HistoricalQualificationContext context,IReadOnlyList<string> fileHashes)
        {
            if(context==null||fileHashes==null||fileHashes.Count<1||fileHashes.Count>10||fileHashes.Any(x=>!HistoricalWebImportResponseMapper.Hash(x)))
                throw new ArgumentException("history_web_source_invalid");
            return new ReadOnlyCollection<HistoricalQualifiedEvidence>(context.Evidence.Where(p=>p.IsQualified&&p.ShopId==context.ShopId&&p.ParserVersion==WebParserVersion&&
                p.Files.Any(fileHashes.Contains)).OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray());
        }
        public static HistoricalWebParserConfiguration ParserConfiguration(IReadOnlyList<HistoricalQualifiedEvidence> proofs,IReadOnlyList<HistoricalSalesImportFile> files)
        {
            if(proofs==null||files==null||proofs.Any(p=>p.ParserVersion!=WebParserVersion||!p.IsQualified))throw new ArgumentException("history_web_configuration_invalid");
            var modern=files.Where(f=>f.Profile==HistoricalSalesProfile.GetnetModern).ToArray();
            var provider=proofs.Where(p=>p.Kind==HistoricalEvidenceKind.ModernTime&&p.Profile==HistoricalSalesProfile.GetnetModern&&p.HasProviderDocument).ToArray();
            var time=provider.Select(p=>p.Interpretation+"|"+p.PolicyVersion).Distinct(StringComparer.Ordinal).ToArray();
            var selected=modern.Length>0&&time.Length==1&&modern.All(f=>provider.Any(p=>p.Files.Contains(f.Sha256)))?provider[0]:null;
            var legacy=files.Where(f=>f.Profile==HistoricalSalesProfile.GetnetLegacy).ToArray();
            return new HistoricalWebParserConfiguration
            {
                LegacyAmountsAreClp=legacy.Length>0&&legacy.All(f=>proofs.Any(p=>p.Kind==HistoricalEvidenceKind.LegacyCurrency&&p.Profile==HistoricalSalesProfile.GetnetLegacy&&p.Files.Contains(f.Sha256))),
                ModernTimeInterpretation=selected?.Interpretation.ToString()??"Unresolved",TemporalPolicyVersion=selected?.PolicyVersion??"unresolved-web-v1",TemporalEvidenceReference=selected?.Reference??""
            };
        }
        public static string ObservedFingerprint(string shop,HistoricalSalesImportAnalysis analysis)
            =>HistoricalQualificationResolver.Resolve(shop,HistoricalQualificationResolver.CreateSubjects(analysis),Array.Empty<HistoricalQualifiedEvidence>()).Fingerprint;
        public static void DemandParserParity(HistoricalSalesImportAnalysis local,HistoricalWebParserSummary remote)
        {
            var s=local?.Summary;
            if(s==null||remote==null||s.SourceRows!=remote.SourceRows||s.AcceptedCount!=remote.Accepted||s.SaleCount!=remote.Sales||s.CancellationCount!=remote.Cancellations||
                s.RejectedCount!=remote.Rejected||s.KnownNetCount!=remote.KnownNetCount||s.KnownRejectedRequestedCount!=remote.KnownRejectedRequestedCount||
                Different(s.GrossClp,remote.SignedGrossClp)||Different(s.PositiveGrossClp,remote.PositiveGrossClp)||Different(s.CancellationGrossClp,remote.CancellationGrossClp)||
                Different(s.KnownNetClp,remote.KnownNetClp)||Different(s.RejectedRequestedClp,remote.RejectedRequestedClp))
                throw new InvalidOperationException("history_web_parser_mismatch");
            // No comparison or inference of business dates across parser/time
            // policies. A local unresolved clock is not a web-policy veto.
        }
        private static bool Different(long? a,long? b)=>a.HasValue&&b.HasValue&&a.Value!=b.Value;
    }
}
