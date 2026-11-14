using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalQualificationResolution
    {
        internal HistoricalQualificationResolution(string shopId,IEnumerable<HistoricalQualificationNeed> missing,IEnumerable<HistoricalQualifiedEvidence> used,
            IDictionary<string,string> merchants,IReadOnlyList<HistoricalQualificationSubject> subjects)
        {
            Missing=new ReadOnlyCollection<HistoricalQualificationNeed>(missing.Distinct().OrderBy(x=>x).ToArray());
            Evidence=new ReadOnlyCollection<HistoricalQualifiedEvidence>(used.Distinct().OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray());
            Merchants=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>(merchants,StringComparer.Ordinal));
            CanPrepare=subjects.Count>0&&Missing.Count==0;
            Fingerprint=HistoricalSalesImportPlan.Hash(new[]{"history-qualified-input-selection-v1",shopId}.Concat(subjects.OrderBy(x=>x.FileSha256,StringComparer.Ordinal).ThenBy(x=>x.AliasKey,StringComparer.Ordinal).SelectMany(s=>new[]{
                "subject",s.FileSha256,s.AliasKey,s.Profile.ToString(),s.ParserVersion,s.FromTicks.ToString(CultureInfo.InvariantCulture),s.ToTicks.ToString(CultureInfo.InvariantCulture),s.ExplicitClp.ToString(),s.ToBusinessTicks?.ToString(CultureInfo.InvariantCulture)??"",s.Interpretation.ToString(),s.TemporalPolicyVersion})).Concat(Evidence.SelectMany(e=>new[]{
                e.Id,e.ShopId,e.Kind.ToString(),e.State.ToString(),e.Issuer.ToString(),e.QualificationVersion,e.MerchantId,e.AliasKey,e.Profile.ToString(),
                e.ParserVersion,string.Join(",",e.Files.OrderBy(x=>x,StringComparer.Ordinal)),e.FromTicks.ToString(CultureInfo.InvariantCulture),e.ToTicks.ToString(CultureInfo.InvariantCulture),
                e.PolicyVersion,e.Reference,e.Interpretation.ToString(),e.NativeStartTicks?.ToString(CultureInfo.InvariantCulture)??"",e.ProviderVerified.ToString(),e.DocumentSha256,e.DocumentUploadId})).ToArray());
        }
        public IReadOnlyList<HistoricalQualificationNeed> Missing { get; }
        public IReadOnlyList<HistoricalQualifiedEvidence> Evidence { get; }
        public IReadOnlyDictionary<string,string> Merchants { get; }
        public bool CanPrepare { get; }
        public string Fingerprint { get; }
    }

    /// <summary>Read-only selection. Fresh server authorization remains mandatory before an economic commit.</summary>
    public static class HistoricalQualificationResolver
    {
        public const string ParserVersion="getnet-ooxml-v1";

        /// <summary>Build observed scopes on the worker from parser facts, before or after a qualified reanalysis.</summary>
        public static IReadOnlyList<HistoricalQualificationSubject> CreateSubjects(HistoricalSalesImportAnalysis analysis)
        {
            if(analysis==null||analysis.HasErrors)throw new ArgumentException("historical_qualification_analysis_invalid");
            var files=new Dictionary<string,HistoricalSalesImportFile>(StringComparer.Ordinal);
            foreach(var file in analysis.Files)
            {
                if(file==null||string.IsNullOrEmpty(file.FileId)||files.ContainsKey(file.FileId))throw new ArgumentException("historical_qualification_analysis_invalid");
                files.Add(file.FileId,file);
            }
            var groups=new Dictionary<Tuple<string,string>,SubjectObservation>();
            foreach(var row in analysis.AcceptedTransactions)
            {
                if(row==null||!files.TryGetValue(row.Source.FileId,out var file)||file.Profile!=row.Profile||row.Timestamp==null)
                    throw new ArgumentException("historical_qualification_analysis_invalid");
                var alias=HistoricalMerchantAssignment.MerchantKey(row.Merchant);var key=Tuple.Create(file.FileId,alias);
                if(!groups.TryGetValue(key,out var observed))
                {
                    observed=new SubjectObservation(file,alias,row.Timestamp.Interpretation,row.Timestamp.PolicyVersion);groups.Add(key,observed);
                    if(groups.Count>2000)throw new ArgumentException("historical_qualification_input_invalid");
                }
                observed.Add(row);
            }
            return new ReadOnlyCollection<HistoricalQualificationSubject>(groups.Values.Select(x=>x.ToSubject()).OrderBy(x=>x.FileSha256,StringComparer.Ordinal).ThenBy(x=>x.AliasKey,StringComparer.Ordinal).ToArray());
        }

        public static HistoricalQualificationResolution Resolve(string shopId,IReadOnlyList<HistoricalQualificationSubject> subjects,
            IReadOnlyList<HistoricalQualifiedEvidence> evidence)
        {
            if(!Guid.TryParse(shopId,out var shop)||shop==Guid.Empty||subjects==null||evidence==null||
               subjects.Count>2000||evidence.Count>200||subjects.Any(x=>x==null)||evidence.Any(x=>x==null)||evidence.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=evidence.Count)
                throw new ArgumentException("historical_qualification_input_invalid");
            if(subjects.GroupBy(x=>x.FileSha256+"|"+x.AliasKey,StringComparer.Ordinal).Any(g=>g.Count()!=1)||
               subjects.GroupBy(x=>x.FileSha256,StringComparer.Ordinal).Any(g=>g.Select(x=>x.Profile+"|"+x.ParserVersion).Distinct(StringComparer.Ordinal).Count()!=1))
                throw new ArgumentException("historical_qualification_input_invalid");
            var missing=new List<HistoricalQualificationNeed>();var used=new HashSet<HistoricalQualifiedEvidence>();
            var merchants=new Dictionary<string,string>(StringComparer.Ordinal);var time=new HashSet<string>(StringComparer.Ordinal);var starts=new HashSet<long>();
            var available=evidence.Where(e=>e.IsQualified&&e.ShopId==shop.ToString("D")).ToArray();
            foreach(var subject in subjects)
            {
                var scoped=available.Where(e=>e.Profile==subject.Profile&&e.ParserVersion==subject.ParserVersion&&e.Files.Contains(subject.FileSha256)&&
                    e.FromTicks<=subject.FromTicks&&e.ToTicks>=subject.ToTicks).ToArray();
                var mapping=scoped.Where(e=>e.Kind==HistoricalEvidenceKind.MerchantMapping&&e.AliasKey==subject.AliasKey).ToArray();
                var ids=mapping.Select(e=>e.MerchantId).Distinct(StringComparer.Ordinal).ToArray();
                if(ids.Length!=1){missing.Add(ids.Length==0?HistoricalQualificationNeed.MerchantMapping:HistoricalQualificationNeed.ConflictingPolicy);continue;}
                var merchant=ids[0];
                if(merchants.TryGetValue(subject.AliasKey,out var prior)&&prior!=merchant){missing.Add(HistoricalQualificationNeed.ConflictingPolicy);continue;}
                merchants[subject.AliasKey]=merchant;foreach(var proof in mapping)used.Add(proof);
                var sameMerchant=scoped.Where(e=>e.MerchantId==merchant).ToArray();
                if(subject.Profile==HistoricalSalesProfile.GetnetModern)
                {
                    if(!subject.ExplicitClp)missing.Add(HistoricalQualificationNeed.ExplicitCurrency);
                    var policies=sameMerchant.Where(e=>e.Kind==HistoricalEvidenceKind.ModernTime&&e.HasProviderDocument&&
                        e.Interpretation!=HistoricalSalesModernTimeInterpretation.Unresolved).ToArray();
                    if(policies.Length==0)missing.Add(HistoricalQualificationNeed.ProviderTime);
                    foreach(var proof in policies){used.Add(proof);time.Add(proof.Interpretation+"|"+proof.PolicyVersion);}
                    if(policies.Length>0&&(!subject.ToBusinessTicks.HasValue||policies.Any(p=>p.Interpretation!=subject.Interpretation||p.PolicyVersion!=subject.TemporalPolicyVersion)))
                        missing.Add(HistoricalQualificationNeed.TimeAnalysis);
                }
                else
                {
                    var currency=sameMerchant.Where(e=>e.Kind==HistoricalEvidenceKind.LegacyCurrency).ToArray();
                    if(currency.Length==0)missing.Add(HistoricalQualificationNeed.LegacyCurrency);
                    foreach(var proof in currency)used.Add(proof);
                }
                var overlap=sameMerchant.Where(e=>e.Kind==HistoricalEvidenceKind.NativeStart&&e.NativeStartTicks.HasValue).ToArray();
                if(overlap.Length==0)missing.Add(HistoricalQualificationNeed.NativeStart);
                foreach(var proof in overlap)
                {
                    used.Add(proof);starts.Add(proof.NativeStartTicks.Value);
                    if(!subject.ToBusinessTicks.HasValue)missing.Add(HistoricalQualificationNeed.TimeAnalysis);
                    else if(subject.ToBusinessTicks.Value>proof.NativeStartTicks.Value)missing.Add(HistoricalQualificationNeed.NativeOverlap);
                }
            }
            if(time.Count>1||starts.Count>1)missing.Add(HistoricalQualificationNeed.ConflictingPolicy);
            return new HistoricalQualificationResolution(shop.ToString("D"),missing,used,merchants,subjects);
        }

        private sealed class SubjectObservation
        {
            private readonly HistoricalSalesImportFile _file;
            private readonly string _alias;
            private readonly HistoricalSalesModernTimeInterpretation _interpretation;
            private readonly string _policy;
            private long _from=long.MaxValue,_to,_businessTo;
            private bool _businessKnown=true;
            internal SubjectObservation(HistoricalSalesImportFile file,string alias,HistoricalSalesModernTimeInterpretation interpretation,string policy)
            { _file=file;_alias=alias;_interpretation=interpretation;_policy=policy; }
            internal void Add(HistoricalSalesTransaction row)
            {
                var timestamp=row.Timestamp;
                var raw=row.Profile==HistoricalSalesProfile.GetnetLegacy?timestamp.BusinessDateTime:timestamp.ExportedLocalDateTime;
                if(!raw.HasValue||timestamp.Interpretation!=_interpretation||timestamp.PolicyVersion!=_policy||
                   row.Profile==HistoricalSalesProfile.GetnetLegacy&&timestamp.NeedsEvidence)
                    throw new ArgumentException("historical_qualification_analysis_invalid");
                _from=Math.Min(_from,raw.Value.Ticks);_to=Math.Max(_to,checked(raw.Value.Ticks+1));
                _businessKnown&=timestamp.BusinessDateTime.HasValue&&!timestamp.NeedsEvidence;
                if(timestamp.BusinessDateTime.HasValue)_businessTo=Math.Max(_businessTo,checked(timestamp.BusinessDateTime.Value.Ticks+1));
            }
            internal HistoricalQualificationSubject ToSubject()=>new HistoricalQualificationSubject(_file.Sha256,_alias,_file.Profile.Value,ParserVersion,
                _from,_to,_file.Profile==HistoricalSalesProfile.GetnetModern,_businessKnown?(long?)_businessTo:null,_interpretation,_policy);
        }
    }
}
