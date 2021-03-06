using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Vereesa.Core.Extensions;

namespace Vereesa.Core.Infrastructure
{
	/// <summary>
	/// Inheriting this class causes a singleton instance of it to automatically start in VereesaClient.cs
	/// </summary>
	public class BotServiceBase
	{
		protected DiscordSocketClient Discord { get; }

		public BotServiceBase(DiscordSocketClient discord)
		{
			Discord = discord;

			BindCommands();
		}

		private void BindCommands()
		{
			var commandMethods = new List<(string command, MethodInfo method)>();
			var memberUpdatedMethods = new List<MethodInfo>();
			var onMessageMethods = new List<MethodInfo>();
			var onReadyMethods = new List<MethodInfo>();

			var allMethods = this.GetType().GetMethods();

			Console.WriteLine(this.GetType().Name);

			foreach (var method in allMethods)
			{
				var commandsAttributes = method.GetCustomAttributes(true).OfType<OnCommandAttribute>();
				commandMethods.AddRange(commandsAttributes.Select(c => (c.Command, method)));

				if (method.GetCustomAttributes(true).OfType<OnMemberUpdatedAttribute>().Any())
				{
					memberUpdatedMethods.Add(method);
				}

				if (method.GetCustomAttribute<OnMessageAttribute>(true) != null)
				{
					onMessageMethods.Add(method);
				}

				if (method.GetCustomAttribute<OnReadyAttribute>(true) != null)
				{
					onReadyMethods.Add(method);
				}
			}

			var commandHandlers = commandMethods.GroupBy(c => c.command).ToList();
			if (commandHandlers.Any())
			{
				Discord.MessageReceived += async (messageForEvaluation) =>
				{
					var matchingHandlers = GetMatchingMessageHandlers(messageForEvaluation, commandHandlers);
					await ExecuteMessageHandlersAsync(messageForEvaluation, matchingHandlers);
				};
			}

			if (memberUpdatedMethods.Any())
			{
				Discord.GuildMemberUpdated += async (userBefore, userAfter) =>
				{
					await ExecuteHandlersAsync(memberUpdatedMethods, new[] { userBefore, userAfter });
				};
			}

			if (onMessageMethods.Any())
			{
				Discord.MessageReceived += async (message) =>
				{
					await ExecuteHandlersAsync(onMessageMethods, new[] { message });
				};
			}

			if (onReadyMethods.Any())
			{
				Discord.Ready += async () =>
				{
					await ExecuteHandlersAsync(onReadyMethods, new object[0]);
				};
			}
		}

		// this code is super similar to the message handler
		private async Task ExecuteHandlersAsync(List<MethodInfo> methods, object[] parameters)
		{
			async Task ExecuteHandler(MethodInfo method, object[] invocationParameters)
			{
				try
				{
					await (Task)method.Invoke(this, invocationParameters);
				}
				catch (Exception)
				{
					// this would be nice to log
				}
			};

			foreach (var method in methods)
			{
				if (method.GetCustomAttribute<AsyncHandlerAttribute>() != null)
				{
					ExecuteHandler(method, parameters);
				}
				else
				{
					await ExecuteHandler(method, parameters);
				}
			}
		}

		private async Task ExecuteMessageHandlersAsync(SocketMessage messageToHandle,
			List<(string command, MethodInfo method)> matchingHandlers)
		{
			async Task ExecuteCommand(string command, MethodInfo handler)
			{
				try
				{
					var handlerParams = new List<object>();
					handlerParams.Add(messageToHandle);

					// var commandPrarams = messageForEvaluation.GetCommandArgs();
					// var matcher = new Regex("(\"(.*?)\"|(.*?))\\s|$");

					// matcher.Match()

					await (Task)handler.Invoke(this, handlerParams.ToArray());
				}
				catch (Exception)
				{
					if (handler.GetCustomAttribute(typeof(CommandUsageAttribute))
						is CommandUsageAttribute usageAttribute)
					{
						await messageToHandle.Channel.SendMessageAsync($"`{command}` usage: {usageAttribute.UsageDescription}");
					}
				}
			};

			foreach (var commandHandler in matchingHandlers)
			{
				if (!UserCanExecute(messageToHandle.Author, commandHandler.method))
				{
					continue;
				}

				// If the command is marked Async, we just fire and forget, because it may be really long 
				// running.
				if (commandHandler.method.GetCustomAttribute<AsyncHandlerAttribute>() != null)
				{
					ExecuteCommand(commandHandler.command, commandHandler.method);
				}
				else
				{
					await ExecuteCommand(commandHandler.command, commandHandler.method);
				}
			}
		}

		private List<(string command, MethodInfo method)> GetMatchingMessageHandlers(
			IMessage messageForEvaluation,
			List<IGrouping<string, (string command, MethodInfo method)>> commandHandlers)
		{
			var command = messageForEvaluation.GetCommand();
			return commandHandlers.Where(ch => ch.Key == command).SelectMany(cd => cd).ToList();
		}

		private bool UserCanExecute(SocketUser caller, MethodInfo method)
		{
			var isAuthorized = true;
			var authorizeAttributes = method.GetCustomAttributes(true).OfType<AuthorizeAttribute>();
			var guildUser = caller as IGuildUser;
			var userRoles = guildUser?.RoleIds.Select(rid => Discord.GetRole(rid)).ToList();

			foreach (var authorizeAttribute in authorizeAttributes)
			{
				if (!userRoles.Any(r => r.Name == authorizeAttribute.RoleName || r.Id == authorizeAttribute.RoleId))
				{
					isAuthorized = false;
				}
			}

			return isAuthorized;
		}

		/// <summary>
		/// Prompts a role for a response. Potentially a long lasting request. This should always be put in an
		/// async event handler.
		/// </summary>
		/// <param name="role">The role responsible for responding to the prompt.</param>
		/// <param name="promptMessage">The message sent to the prompted role.</param>
		/// <param name="channel">The channel in which to prompt the role.</param>
		/// <param name="timeoutMs">Duration in milliseconds to wait for a response.</param>
		/// <returns>The first message sent by a person with the prompted role in the selected channel. 
		/// Null if no one in the responsible role responds before the timeout.</returns>
		protected Task<IMessage> Prompt(SocketRole role, string promptMessage, IMessageChannel channel,
			int timeoutMs = 15000) => Prompt(role, promptMessage, channel,
				(u) => ((IGuildUser)u).RoleIds.Contains(role.Id), timeoutMs);

		/// <summary>
		/// Prompts a user for a response. Potentially a long lasting request. This should always be put in an
		/// async event handler.
		/// </summary>
		/// <param name="user">The user responsible for responding to the prompt.</param>
		/// <param name="promptMessage">The message sent to the prompted user.</param>
		/// <param name="channel">The channel in which to prompt the user.</param>
		/// <param name="timeoutMs">Duration in milliseconds to wait for a response.</param>
		/// <returns>The first message sent by the prompted user in the selected channel. Null if there is no response
		/// from the responsible user before the timeout.</returns>
		protected Task<IMessage> Prompt(IUser user, string promptMessage, IMessageChannel channel,
			int timeoutMs = 15000) => Prompt(user, promptMessage, channel, (u) => u.Id == user.Id, timeoutMs);

		private Task<IMessage> Prompt(IMentionable responsible, string promptMessage,
			IMessageChannel channel, Func<IUser, bool> authorIsResponsible, int timeoutMs)
		{
			return Task.Run(async () =>
			{
				IMessage response = null;

				Task AwaitResponse(IMessage msg)
				{
					if (msg.Channel.Id == channel.Id && authorIsResponsible(msg.Author))
					{
						response = msg;
					}

					return Task.CompletedTask;
				}

				Discord.MessageReceived += AwaitResponse;
				await channel.SendMessageAsync($"{responsible.Mention} {promptMessage}");

				var sw = new Stopwatch();
				sw.Start();

				while (response == null && sw.ElapsedMilliseconds < timeoutMs)
				{
					await Task.Delay(250);
				}

				Discord.MessageReceived -= AwaitResponse;
				sw.Stop();

				return response;
			});
		}
	}
}