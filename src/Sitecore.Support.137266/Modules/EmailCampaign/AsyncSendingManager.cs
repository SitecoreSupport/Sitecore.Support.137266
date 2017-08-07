namespace Sitecore.Support.Modules.EmailCampaign
{
  using System.Collections.Generic;
  using System.Linq;
  using Sitecore.Data;
  using Sitecore.Diagnostics;
  using Sitecore.Jobs;
  using Sitecore.Modules.EmailCampaign.Core;
  using Sitecore.Modules.EmailCampaign.Core.Dispatch;
  using Sitecore.Modules.EmailCampaign.Messages;
  using Sitecore.Modules.EmailCampaign.Recipients;
  using Sitecore.StringExtensions;
  using System;

  public sealed class AsyncSendingManager : SendingManager
  {
    private readonly ExpressionUtil<AsyncSendingManager> _expressionUtil = new ExpressionUtil<AsyncSendingManager>();

    #region Public static methods

    [NotNull]
    public static List<ID> GetMessagesInProgress()
    {
      // Get active jobs.
      var jobs = JobManager.GetJobs();

      // Extract message ids of jobs related to message dispatching.
      var messageIds = jobs
          .Select(job => job.Options.CustomData)
          .OfType<SendingProcessData>()
          .Select(processData => processData.MessageId)
          .ToList();

      return messageIds;
    }

    #endregion

    #region Constructors

    public AsyncSendingManager(MessageItem message)
        : base(message)
    {
    }

    public AsyncSendingManager(bool checkConnection, MessageItem message)
        : base(checkConnection, message)
    {
    }

    public AsyncSendingManager(MessageItem message, bool isService)
        : base(message, isService)
    {
    }

    public AsyncSendingManager(bool checkConnection, MessageItem message, bool isService)
        : base(checkConnection, message, isService, ExM.Framework.Diagnostics.Logger.Instance)
    {
    }

    #endregion Constructors

    #region Public methods

    public override SendingProcessData SendTestMessage(List<string> addresses, bool personalize)
    {
      return StartSendingJob(_expressionUtil.GetMemberName(x => SendTestCore(null, default(bool))), addresses, personalize);
    }

    public override SendingProcessData SendTestMessage(string address, int number)
    {
      return StartSendingJob(_expressionUtil.GetMemberName(x => SendPersonalizedTestCore(null, default(int))), address, number);
    }

    public override SendingProcessData SendStandardMessage(RecipientId recipientId)
    {
      Assert.ArgumentNotNull(recipientId, "recipientId");

      var jobParameter = string.Format("{0}_{1}_{2}", Message.ShortID, recipientId, Guid.NewGuid());
      var jobName = EcmTexts.SendingThreadName.FormatWith(jobParameter);

      return StartSendingJob(_expressionUtil.GetMemberName(x => SendStandardCore(null)), jobName, recipientId);
    }

    public override SendingProcessData SendMessage()
    {
      return StartSendingJob(_expressionUtil.GetMemberName(x => SendCore(default(int))), 0);
    }

    public override SendingProcessData SendMessage(float abTestPercentage)
    {
      Logger.TraceInfo("AsyncSendingManager: SendMessage() start.");

      return StartSendingJob(_expressionUtil.GetMemberName(x => SendCore(default(int))), abTestPercentage);
    }

    public override SendingProcessData ResumeMessageSending()
    {
      var hasQueued = Message.Source.State == MessageState.Sending || Message.Source.State == MessageState.Active;
      var jobName = hasQueued ? _expressionUtil.GetMemberName(x => ResumeQueuedSendingCore()) :
          _expressionUtil.GetMemberName(x => ResumeUnqueuedSendingCore());

      var result = StartSendingJob(jobName);
      return result;
    }

    #endregion Public methods

    #region Internal methods

    internal override SendingProcessData SendPersonalizedTestCore(string address, int number)
    {
      if (Context.Job != null)
      {
        Context.Job.Options.CustomData = ProcessData;
      }

      var sendingData = base.SendPersonalizedTestCore(address, number);

      if (sendingData == null)
      {
        SetJobError(EcmTexts.Localize(EcmTexts.SendingFailed));
        return null;
      }

      if (!sendingData.IsCompleted)
      {
        SetJobError(sendingData.Errors);
      }

      return sendingData;
    }

    internal override SendingProcessData SendTestCore(List<string> addresses, bool personalize)
    {
      if (Context.Job != null)
      {
        Context.Job.Options.CustomData = ProcessData;
      }

      var sendingData = base.SendTestCore(addresses, personalize);

      if (sendingData == null)
      {
        SetJobError(EcmTexts.Localize(EcmTexts.SendingFailed));
        return null;
      }

      if (!sendingData.IsCompleted)
      {
        SetJobError(sendingData.Errors);
      }

      return sendingData;
    }

    internal override SendingProcessData SendStandardCore(RecipientId recipientId)
    {
      if (Context.Job != null)
      {
        Context.Job.Options.CustomData = ProcessData;
      }

      return CheckResult(base.SendStandardCore(recipientId));
    }

    internal override SendingProcessData SendCore(float abTestPercentage)
    {
      if (Context.Job != null)
      {
        Context.Job.Options.CustomData = ProcessData;
      }

      return CheckResult(base.SendCore(abTestPercentage));
    }

    internal override SendingProcessData ResumeQueuedSendingCore()
    {
      if (Context.Job != null)
      {
        Context.Job.Options.CustomData = ProcessData;
      }

      return CheckResult(base.ResumeQueuedSendingCore());
    }

    internal override SendingProcessData ResumeUnqueuedSendingCore()
    {
      if (Context.Job != null)
      {
        Context.Job.Options.CustomData = ProcessData;
      }

      return CheckResult(base.ResumeUnqueuedSendingCore());
    }

    #endregion Internal methods

    #region Private methods

    private SendingProcessData CheckResult(SendingProcessData sendingData)
    {
      if (sendingData == null)
      {
        SetJobError(EcmTexts.Localize(EcmTexts.SendingFailed));
        return null;
      }

      if (!sendingData.IsCompleted)
      {
        SetJobError(EcmTexts.Localize(EcmTexts.MessageNotSent) + " " + sendingData.Warnings + sendingData.Errors);
      }

      return sendingData;
    }

    private SendingProcessData StartSendingJob(string funcName, params object[] args)
    {
      var jobName = EcmTexts.SendingThreadName.FormatWith(Message.ShortID);
      return StartSendingJob(funcName, jobName, args);
    }

    private SendingProcessData StartSendingJob(string funcName, string jobName, params object[] args)
    {
      JobHelper.TryStartJob(jobName, funcName, this, args);
      return new SendingProcessData(new ID(Message.MessageId));
    }

    private void SetJobError(string msg)
    {
      Context.Job.Status.Messages.Clear();
      Context.Job.Status.Messages.Add(msg);
      Context.Job.Status.Failed = true;
    }

    #endregion Private methods
  }
}
