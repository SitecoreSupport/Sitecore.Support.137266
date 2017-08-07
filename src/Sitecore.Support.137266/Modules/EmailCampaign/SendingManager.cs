namespace Sitecore.Support.Modules.EmailCampaign
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Threading.Tasks;
  using Sitecore.Data;
  using Sitecore.Diagnostics;
  using Sitecore.EDS.Core.Senders;
  using Sitecore.ExM.Framework.Diagnostics;
  using Sitecore.Modules.EmailCampaign.Core;
  using Sitecore.Modules.EmailCampaign.Core.Dispatch;
  using Sitecore.Modules.EmailCampaign.Core.Pipelines.DispatchNewsletter;
  using Sitecore.Modules.EmailCampaign.Exceptions;
  using Sitecore.Modules.EmailCampaign.Factories;
  using Sitecore.Modules.EmailCampaign.Messages;
  using Sitecore.Modules.EmailCampaign.Recipients;
  using Sitecore.Pipelines;
  using Sitecore.SecurityModel;
  using IDispatchManager = Sitecore.EDS.Core.Dispatch.IDispatchManager;
  using Sitecore.Modules.EmailCampaign;

  public class SendingManager : ISendingManager
  {
    [NotNull]
    private readonly IDispatchManager _dispatchManager;

    [NotNull]
    private readonly ISenderManager _senderManager;

    [NotNull]
    private SendingProcessData _processData;

    protected ILogger Logger { get; private set; }

    protected JobHelper JobHelper { get; private set; }

    internal bool DedicatedInstance { get; set; }

    internal MessageItem Message { get; set; }

    internal SendingProcessData ProcessData
    {
      get { return _processData ?? (_processData = new SendingProcessData(new ID(Message.MessageId))); }
    }

    internal virtual SendingProcessData SendPersonalizedTestCore(string address, int number)
    {
      Assert.ArgumentNotNullOrEmpty(address, "address");
      Assert.ArgumentCondition(number > 0, "number", "The 'number' argument cannot be less than 1.");

      var contacts = Message.SubscribersIds.Value;

      Message.To = address;

      var args = new DispatchNewsletterArgs(Message, ProcessData)
      {
        IsTestSend = true,
        TestRecipients = Util.GetRandomContacts(contacts, number).ToList().ConvertAll(x => new RecipientInfo
        {
          RecipientId = x
        })
      };

      RunPipeline(args);

      return args.ProcessData;
    }

    internal virtual SendingProcessData SendTestCore(List<string> addresses, bool personalize)
    {
      throw new NotImplementedException("This method must not be used as this is a patched implementation of SendingManager used by EAPlans only");
    }

    internal virtual SendingProcessData SendStandardCore(RecipientId recipientId)
    {
      Assert.ArgumentNotNull(recipientId, "recipientId");

      // Only proceed sending if the message is active.
      if (Message.State != MessageState.Active)
      {
        return null;
      }

      var args = new DispatchNewsletterArgs(Message, ProcessData);

      if (GlobalSettings.NoSend)
      {
        args.AbortSending(EcmTexts.Localize(EcmTexts.SendingDisabled), false, Logger);
        return args.ProcessData;
      }

      args.RequireInitialMovement = false;
      args.RequireFinalMovement = false;

      args.AllowNotifications = false;

      args.DedicatedInstance = DedicatedInstance;

      if (args.Message.MessageType != MessageType.Triggered || ID.IsNullOrEmpty(args.Message.CampaignId))
      {
        new DeployAnalytics().Process(args);
        new PublishDispatchItems().Process(args);
      }

      //Triggered messages do not have a test percentage set, so the recipient should always
      //be an ab test recipient in case it's an ab test message
      var abTestMessage = args.Message as ABTestMessage;
      if (args.Message.MessageType == MessageType.Triggered && abTestMessage != null && abTestMessage.IsTestConfigured)
      {
        args.AbTestPercentage = 100;
      }

      using (new SecurityDisabler())
      {
        EcmFactory.GetDefaultFactory().Bl.DispatchManager.AddRecipientToDispatchQueue(args, recipientId);
      }

      args.Queued = true;

      RunPipeline(args);

      return args.ProcessData;
    }

    internal virtual SendingProcessData SendCore(float abTestPercentage)
    {
      Logger.TraceInfo("SendingManager: SendCore() begin");

      var args = new DispatchNewsletterArgs(Message, ProcessData)
      {
        AbTestPercentage = abTestPercentage,
        Queued = false,
        RequireInitialMovement = true,
        RequireFinalMovement = true,
        DedicatedInstance = DedicatedInstance
      };

      RunPipeline(args);

      Logger.TraceInfo("SendingManager: SendCore() end");

      return args.ProcessData;
    }

    internal virtual SendingProcessData ResumeQueuedSendingCore()
    {
      var args = new DispatchNewsletterArgs(Message, ProcessData)
      {
        DedicatedInstance = DedicatedInstance,
        Queued = true,
        PauseAfterQueueing = true,
        RequireInitialMovement = false,
        RequireFinalMovement = true
      };

      RunPipeline(args);

      return args.ProcessData;
    }

    internal virtual SendingProcessData ResumeUnqueuedSendingCore()
    {
      var args = new DispatchNewsletterArgs(Message, ProcessData);

      var abTestMessage = Message as ABTestMessage;
      if (abTestMessage != null)
      {
        var abnTest = new AbnTest(abTestMessage);
        if (abnTest.IsTestConfigured())
        {
          args.AbTestPercentage = Message.TestSizePercent;
        }
      }

      args.DedicatedInstance = DedicatedInstance;
      args.Queued = false;
      args.RequireInitialMovement = false;
      args.RequireFinalMovement = true;

      RunPipeline(args);

      return args.ProcessData;
    }

    protected bool HasAccessToMailServer(bool checkConnection, out string error)
    {
      return CheckMailServer(out error);
    }

    private void RunPipeline(DispatchNewsletterArgs args)
    {
      var dispatchInfo = new DispatchInfo(Logger);
      dispatchInfo.LogDispatchStarted(args);

      try
      {
        const string pipeline = "DispatchNewsletter";
        CorePipeline.Run(pipeline, args);

        if (args.Aborted)
        {
          Logger.LogInfo(string.Format(EcmTexts.PipelineAborted, pipeline) + " " + args.ProcessData.Errors);
        }
      }
      catch (Exception e)
      {
        Logger.LogError(e);
      }

      if (args.SendingAborted)
      {
        ProcessData.State = SendingState.Paused;
        Logger.LogInfo(ProcessData.Errors);
        Logger.LogInfo(ProcessData.Warnings);
      }
      else
      {
        ProcessData.State = SendingState.Finished;
      }

      dispatchInfo.LogDispatchFinished(args);
    }

    private bool CheckMailServer(out string error)
    {
      error = string.Empty;

      if (!_dispatchManager.IsConfigured)
      {
        error = EcmTexts.Localize(EcmTexts.DispatchManagerNotConfigured);
        return false;
      }

      var validationResult = _dispatchManager.ValidateDispatchAsync().Result;
      if (!validationResult)
      {
        error = EcmTexts.Localize(EcmTexts.FailedConnectToEmailServer);
      }

      return validationResult;
    }

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SendingManager" /> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <exception cref="Sitecore.Modules.EmailCampaign.Exceptions.EmailCampaignException">
    ///  When connection to SMTP could not be established.
    ///  Sender validation has failed.
    ///  Sender email is invalid or sender registration is in progress.
    /// </exception>
    public SendingManager(MessageItem message)
        : this(true, message, false, ExM.Framework.Diagnostics.Logger.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SendingManager" /> class.
    /// </summary>
    /// <param name="checkConnection">if set to <c>true</c> [check connection].</param>
    /// <param name="message">The message.</param>
    /// <exception cref="Sitecore.Modules.EmailCampaign.Exceptions.EmailCampaignException">
    ///  When connection to SMTP could not be established.
    ///  Sender validation has failed.
    ///  Sender email is invalid or sender registration is in progress.
    /// </exception>
    public SendingManager(bool checkConnection, MessageItem message)
        : this(checkConnection, message, false, ExM.Framework.Diagnostics.Logger.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SendingManager" /> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="isService">if set to <c>true</c> [is service].</param>
    /// <exception cref="Sitecore.Modules.EmailCampaign.Exceptions.EmailCampaignException">
    ///  When connection to SMTP could not be established.
    ///  Sender validation has failed.
    ///  Sender email is invalid or sender registration is in progress.
    /// </exception>
    public SendingManager(MessageItem message, bool isService)
        : this(true, message, isService, ExM.Framework.Diagnostics.Logger.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SendingManager" /> class.
    /// </summary>
    /// <param name="checkConnection">if set to <c>true</c> [check connection].</param>
    /// <param name="message">The message.</param>
    /// <param name="isService">if set to <c>true</c> [is service].</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="Sitecore.Modules.EmailCampaign.Exceptions.EmailCampaignException">
    ///  When connection to SMTP could not be established.
    ///  Sender validation has failed.
    ///  Sender email is invalid or sender registration is in progress.
    /// </exception>
    public SendingManager(bool checkConnection, [NotNull] MessageItem message, bool isService, [NotNull] ILogger logger)
        : this(checkConnection, message, isService, logger,
            Configuration.Factory.CreateObject("exm/eds/senderManager", true) as ISenderManager,
            Configuration.Factory.CreateObject("exm/eds/dispatchManager", true) as IDispatchManager)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SendingManager" /> class.
    /// </summary>
    /// <param name="checkConnection">if set to <c>true</c> [check connection].</param>
    /// <param name="message">The message.</param>
    /// <param name="isService">if set to <c>true</c> [is service].</param>
    /// <param name="logger">The logger.</param>
    /// <param name="senderManager">The Sender manager.</param>
    /// <param name="dispatchManager">The Dispatch manager.</param>
    /// <exception cref="Sitecore.Modules.EmailCampaign.Exceptions.EmailCampaignException">
    ///  When connection to SMTP could not be established.
    ///  Sender validation has failed.
    ///  Sender email is invalid or sender registration is in progress.
    /// </exception>
    public SendingManager(bool checkConnection, [NotNull] MessageItem message, bool isService, [NotNull] ILogger logger,
        [NotNull] ISenderManager senderManager, [NotNull] IDispatchManager dispatchManager)
    {
      Assert.ArgumentNotNull(message, "message");
      Assert.ArgumentNotNull(logger, "logger");
      Assert.ArgumentNotNull(senderManager, "senderManager");
      Assert.ArgumentNotNull(senderManager, "dispatchManager");

      Logger = logger;
      JobHelper = new JobHelper(Logger);
      _senderManager = senderManager;
      _dispatchManager = dispatchManager;

      string error;
      if (!HasAccessToMailServer(checkConnection, out error))
      {
        throw new EmailCampaignException(error);
      }

      FileCache.Clear();

      Util.CheckAnalytics();

      Message = message;
      DedicatedInstance = isService;

      if (!_senderManager.IsConfigured)
      {
        return;
      }

      error = EcmTexts.FailedToValidateSender;
      SenderValidationStatus result;

      try
      {
        result = Task.Run(() => _senderManager.ValidateSenderAsync(message.FromAddress)).Result;
      }
      catch (Exception exception)
      {
        Logger.LogError(error, exception);
        throw new EmailCampaignException(error);
      }

      switch (result)
      {
        case SenderValidationStatus.Invalid:
          error = EcmTexts.FailedToValidateInvalidSender;
          Logger.LogError(error);
          throw new InvalidSenderException(error);

        case SenderValidationStatus.Pending:
          error = EcmTexts.FailedToValidatePendingSender;
          Logger.LogError(error);
          throw new InvalidSenderException(error);

        default:
          Logger.LogInfo(string.Format("Registered email: {0}", message.FromAddress));
          break;
      }
    }

    #endregion Constructors

    #region Public methods

    public bool HasAccessToMailServer(out string error)
    {
      return CheckMailServer(out error);
    }

    public virtual SendingProcessData SendTestMessage(List<string> addresses, bool personalize)
    {
      return SendTestCore(addresses, personalize);
    }

    public virtual SendingProcessData SendTestMessage(string address, int number)
    {
      return SendPersonalizedTestCore(address, number);
    }

    public virtual SendingProcessData SendStandardMessage(RecipientId recipientId)
    {
      Assert.ArgumentNotNull(recipientId, "recipientId");

      return SendStandardCore(recipientId);
    }

    public virtual SendingProcessData SendMessage()
    {
      return SendCore(0);
    }

    public virtual SendingProcessData SendMessage(float abTestPercentage)
    {
      return SendCore(abTestPercentage);
    }

    public virtual SendingProcessData ResumeMessageSending()
    {
      var hasQueued = Message.Source.State == MessageState.Sending || Message.Source.State == MessageState.Active;
      var result = hasQueued ? ResumeQueuedSendingCore() : ResumeUnqueuedSendingCore();
      return result;
    }

    #endregion Public methods
  }
}
