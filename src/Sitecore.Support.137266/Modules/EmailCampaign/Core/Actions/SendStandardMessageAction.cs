namespace Sitecore.Support.Modules.EmailCampaign.Core.Actions
{
  using Sitecore.Analytics.Automation;
  using Sitecore.Analytics.Tracking;
  using Sitecore.Diagnostics;
  using Sitecore.ExM.Framework.Diagnostics;
  using Sitecore.Modules.EmailCampaign;
  using Sitecore.Modules.EmailCampaign.Exceptions;
  using Sitecore.Modules.EmailCampaign.Xdb;
  using System;

  public class SendStandardMessageAction : IAutomationAction
  {
    private readonly ILogger logger;

    public SendStandardMessageAction() : this(Logger.Instance)
    {
    }

    public SendStandardMessageAction(ILogger logger)
    {
      Assert.ArgumentNotNull(logger, "logger");
      this.logger = logger;
    }

    public AutomationActionResult Execute(AutomationActionContext automationStatesContext)
    {
      Assert.ArgumentNotNull(automationStatesContext, "automationStatesContext");
      string g = automationStatesContext.Parameters["StandardMessageId"];
      Contact contact = automationStatesContext.Contact;
      if ((contact == null) || string.IsNullOrEmpty(contact.Identifiers.Identifier))
      {
        this.LogError("An engagement automation plan action failed as Email Experience Manager could not find the username associated with the action.");
        return AutomationActionResult.Continue;
      }
      try
      {
        new Sitecore.Support.Modules.EmailCampaign.AsyncSendingManager(Factory.GetMessage(g)).SendStandardMessage(new XdbContactId(contact.ContactId));
      }
      catch (EmailCampaignException exception)
      {
        this.logger.LogError(exception);
      }
      return AutomationActionResult.Continue;
    }

    private void LogError(string error)
    {
      this.logger.LogError("SendStandardMessageAction: " + error);
    }
  }
}
