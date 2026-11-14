using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Security;
using Win7POS.Data.Online;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed partial class HistoricalSalesImportViewModel
    {
        private HistoricalWebReviewPlan _webReview;
        private string _historyWriterAuthority="local";
        private bool IsWebWriter=>_historyWriterAuthority=="web";
        private bool WebReviewCanApply=>IsWebWriter&&_webReview?.CanCommit==true&&_webReview.WriterReady&&_webReview.Summary.NewCount>0&&
            _webReview.Summary.Conflicts==0&&_webReview.Summary.Blocked==0;
        private void ObserveHistoryWriter(string authority,bool inconsistent=false)
        {
            var next=inconsistent?"frozen":authority??"local";
            // A retained local cache cannot reopen the local writer after the
            // shared writer was observed. Financial admission is always fresh.
            if(_historyWriterAuthority=="web"&&next=="local")return;
            if(next!=_historyWriterAuthority)
            {
                _plan=null;_webReview=null;_planForm=null;_planConfigDigest=null;_planQualificationFingerprint=null;
                if(_analysis!=null){SetRequirements(T("reviewStale"));SetStatus(0,T("reviewStale"));}
            }
            _historyWriterAuthority=next;
            _cloudLocalWriterClosed=_historyWriterAuthority!="local";
            Changed(nameof(CanImport));Changed(nameof(LocalHistoryWriterClosed));Changed(nameof(HistoryWriteNotice));
        }
        public string HistoryWriteNotice=>IsWebWriter?T("webWriterNotice"):T("frozenPreparation");

        private async Task PreviewWebAsync(CancellationToken ct)
        {
            var progress=await _service.PrepareHistoricalWebImportAsync(_analysis,_qualification.ConfigDigest,_session,ct).ConfigureAwait(true);
            DemandPublication(ct,PermissionCodes.RegisterImportHistory);_plan=null;_webReview=progress.Review;_planForm=null;
            if(progress.Receipt!=null)
            {
                _webReview=null;_importFinished=true;SetRequirements("");Status=WebReceiptText(progress.Receipt);return;
            }
            if(progress.Review!=null)
            {
                var summary=progress.Review.Summary;
                Summary=_analysisSummary+Environment.NewLine+string.Format(CultureInfo.CurrentCulture,T("applyPreview"),summary.NewCount,summary.DuplicateCount,summary.Conflicts,summary.NewGrossClp,summary.KnownNewNetClp,summary.KnownNewNetCount);
                if(summary.NewCount==0&&summary.DuplicateCount>0&&summary.DuplicateCount==summary.Total&&summary.Conflicts==0&&summary.Blocked==0)
                {
                    _webReview=null;_importFinished=true;SetRequirements("");Status=T("alreadyImported");return;
                }
                if(WebReviewCanApply){_planForm=FormFingerprint;SetRequirements(T("requirementsReady"));Status=T("confirmReady");return;}
                SetRequirements(string.Join(Environment.NewLine,progress.Reasons.Select(WebRequirement).DefaultIfEmpty(T("webQualificationRequired")).Distinct())+Environment.NewLine+T("requirementsAction"));
                Status=summary.Conflicts>0?T("conflicts"):T("needsEvidence");return;
            }
            _webReview=null;
            if(progress.State=="failed"||progress.State=="cancelled"||progress.State=="rejected"||progress.State=="expired")
            {SetRequirements(T("webPreparationFailed"));Status=T("webPreparationFailed");return;}
            SetRequirements(T("webCheckProgress"));Status=T("webPreparing");
        }
        private static string WebRequirement(string reason)
        {
            switch(reason)
            {
                case "history_merchant_unverified":return T("requireMerchant");
                case "history_native_overlap":return T("requireNativeOverlap");
                case "history_writer_not_ready":return T("frozenPreparation");
                case "history_sources_unverified":return T("webCheckProgress");
                default:return T("webQualificationRequired");
            }
        }
        private static string WebReceiptText(HistoricalWebImportReceipt receipt)
            =>string.Format(CultureInfo.CurrentCulture,T("webReceipt"),receipt.OperationId,receipt.Summary.NewCount,receipt.Summary.DuplicateCount,receipt.Revision);
        private async Task ApplyWebAsync()
        {
            var review=_webReview;var form=_planForm;
            if(IsBusy||_confirmingApply||!WebReviewCanApply||form!=FormFingerprint||!CanImport)return;
            _confirmingApply=true;UpdateCommands();var attempted=false;
            try
            {
                if(!ApplyConfirmDialog.ShowConfirm(OwnerWindow??DialogOwnerHelper.GetSafeOwner(),T("apply"),Shop+Environment.NewLine+Summary+Environment.NewLine+T("webConfirmNotice")))return;
                if(!ReferenceEquals(review,_webReview)||form!=FormFingerprint||!CanImport){_webReview=null;Status=T("reviewStale");return;}
                attempted=true;
                await ExecuteAsync(async ct=>
                {
                    var receipt=await _service.CommitHistoricalWebImportAsync(review,_session,ct).ConfigureAwait(true);
                    DemandPublication(ct,PermissionCodes.RegisterImportHistory);
                    _webReview=null;_importFinished=true;SetRequirements("");Status=WebReceiptText(receipt);
                }).ConfigureAwait(true);
            }
            finally{if(attempted&&ReferenceEquals(_webReview,review))_webReview=null;_confirmingApply=false;UpdateCommands();}
        }
    }
}
