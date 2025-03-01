using DocumentFormat.OpenXml.Office2010.ExcelAc;
using FastWiki.Service.Application.Storage.Queries;
using FastWiki.Service.Domain.Storage.Aggregates;
using FastWiki.Service.Infrastructure.Helper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FastWiki.Service.Service;

/// <inheritdoc />
public sealed class ChatApplicationService(WikiMemoryService wikiMemoryService, IMemoryCache memoryCache)
    : ApplicationService<ChatApplicationService>, IChatApplicationService
{
    /// <inheritdoc />
    [Authorize]
    public async Task CreateAsync(CreateChatApplicationInput input)
    {
        var command = new CreateChatApplicationCommand(input);

        await EventBus.PublishAsync(command);
    }

    /// <inheritdoc />
    [Authorize]
    public async Task RemoveAsync(string id)
    {
        var command = new RemoveChatApplicationCommand(id);

        await EventBus.PublishAsync(command);
    }

    /// <inheritdoc />
    [Authorize]
    public async Task UpdateAsync(UpdateChatApplicationInput input)
    {
        var command = new UpdateChatApplicationCommand(input);

        await EventBus.PublishAsync(command);
    }

    /// <inheritdoc />
    [Authorize]
    public async Task<PaginatedListBase<ChatApplicationDto>> GetListAsync(int page, int pageSize)
    {
        var query = new ChatApplicationQuery(page, pageSize);

        await EventBus.PublishAsync(query);

        return query.Result;
    }

    /// <inheritdoc />
    [Authorize]
    public async Task<ChatApplicationDto> GetAsync(string id)
    {
        var query = new ChatApplicationInfoQuery(id);

        await EventBus.PublishAsync(query);

        return query.Result;
    }

    public async Task<ChatApplicationDto> GetChatShareApplicationAsync(string chatShareId)
    {
        var query = new ChatShareApplicationQuery(chatShareId);

        await EventBus.PublishAsync(query);

        return query.Result;
    }

    /// <inheritdoc />
    public async Task CreateChatDialogAsync(CreateChatDialogInput input)
    {
        var command = new CreateChatDialogCommand(input);

        await EventBus.PublishAsync(command);
    }

    /// <param name="applicationId"></param>
    /// <param name="all"></param>
    /// <inheritdoc />
    [Authorize]
    public async Task<List<ChatDialogDto>> GetChatDialogAsync(string applicationId, bool all)
    {
        var query = new ChatDialogQuery(applicationId, all);

        await EventBus.PublishAsync(query);

        return query.Result;
    }

    /// <param name="chatId"></param>
    /// <inheritdoc />
    public async Task<List<ChatDialogDto>> GetChatShareDialogAsync(string chatId)
    {
        var query = new ChatShareDialogQuery(chatId);

        await EventBus.PublishAsync(query);

        return query.Result;
    }

    /// <inheritdoc />
    [Authorize]
    public async IAsyncEnumerable<CompletionsDto> CompletionsAsync(CompletionsInput input)
    {
        var chatApplicationQuery = new ChatApplicationInfoQuery(input.ChatId);

        await EventBus.PublishAsync(chatApplicationQuery);

        if (chatApplicationQuery.Result == null)
        {
            throw new UserFriendlyException("应用Id不存在");
        }

        var prompt = string.Empty;

        var sourceFile = new List<FileStorage>();
        // 如果为空则不使用知识库
        if (chatApplicationQuery.Result.WikiIds.Count != 0)
        {
            var memoryServerless = GetRequiredService<MemoryServerless>();

            var filters = chatApplicationQuery.Result.WikiIds
                .Select(chatApplication => new MemoryFilter().ByTag("wikiId", chatApplication.ToString())).ToList();


            var result = await memoryServerless.SearchAsync(input.Content, "wiki", filters: filters, limit: 3, minRelevance: chatApplicationQuery.Result.Relevancy);

            var fileIds = new List<long>();

            result.Results.ForEach(x =>
            {
                // 获取fileId
                var fileId = x.Partitions.Select(x => x.Tags.FirstOrDefault(x => x.Key == "fileId"))
                    .FirstOrDefault(x => !x.Value.IsNullOrEmpty())
                    .Value.FirstOrDefault();

                if (!fileId.IsNullOrWhiteSpace() && long.TryParse(fileId, out var id))
                {
                    fileIds.Add(id);
                }

                prompt += string.Join(Environment.NewLine, x.Partitions.Select(x => x.Text));
            });

            if (result.Results.Count == 0 &&
                !string.IsNullOrWhiteSpace(chatApplicationQuery.Result.NoReplyFoundTemplate))
            {
                yield return new CompletionsDto()
                {
                    Content = chatApplicationQuery.Result.NoReplyFoundTemplate
                };
                yield break;
            }

            var tokens = TokenHelper.GetGptEncoding().Encode(prompt);

            prompt = TokenHelper.GetGptEncoding()
                .Decode(tokens.Take(chatApplicationQuery.Result.MaxResponseToken).ToList());

            input.Content = chatApplicationQuery.Result.Template.Replace("{{quote}}", prompt)
                .Replace("{{question}}", input.Content);

            if (fileIds.Count > 0 && chatApplicationQuery.Result.ShowSourceFile)
            {
                var fileQuery = new StorageInfosQuery(fileIds);

                await EventBus.PublishAsync(fileQuery);

                sourceFile.AddRange(fileQuery.Result);
            }
        }

        var chatStream = wikiMemoryService.CreateOpenAIChatCompletionService(chatApplicationQuery.Result.ChatModel);

        var chatHistory = new ChatHistory();

        if (!chatApplicationQuery.Result.Prompt.IsNullOrWhiteSpace())
        {
            chatHistory.AddSystemMessage(chatApplicationQuery.Result.Prompt);
        }

        // TODO: 后期可修改为可配置
        var historyQuery = new ChatDialogHistoryQuery(input.ChatDialogId, 1, 3);

        await EventBus.PublishAsync(historyQuery);

        foreach (var message in historyQuery.Result.Result)
        {
            if (message.Current)
            {
                chatHistory.AddUserMessage(message.Content);
            }
            else
            {
                chatHistory.AddAssistantMessage(message.Content);
            }
        }

        chatHistory.AddUserMessage(chatApplicationQuery.Result.Template.Replace("{{quote}}", prompt)
            .Replace("{{question}}", input.Content));

        await foreach (var item in chatStream.GetStreamingChatMessageContentsAsync(chatHistory))
        {
            yield return new CompletionsDto()
            {
                Content = item.Content ?? string.Empty,
            };
            await Task.Delay(1);
        }

        if (sourceFile.Count > 0)
        {
            yield return new CompletionsDto()
            {
                SourceFile = sourceFile.Select(x => new SourceFileDto()
                {
                    Name = x.Name,
                    FilePath = x.Path,
                    FileId = x.Id.ToString()
                }).ToList()
            };
        }
    }

    public async IAsyncEnumerable<CompletionsDto> ChatShareCompletionsAsync(ChatShareCompletionsInput input)
    {
        var chatShareInfoQuery = new ChatShareInfoQuery(input.ChatShareId);

        await EventBus.PublishAsync(chatShareInfoQuery);

        var chatApplicationQuery = new ChatApplicationInfoQuery(chatShareInfoQuery.Result.ChatApplicationId);

        await EventBus.PublishAsync(chatApplicationQuery);

        if (chatApplicationQuery.Result == null)
        {
            throw new UserFriendlyException("应用Id不存在");
        }

        var prompt = string.Empty;

        var sourceFile = new List<FileStorage>();

        // 如果为空则不使用知识库
        if (chatApplicationQuery.Result.WikiIds.Count != 0)
        {
            var memoryServerless = GetRequiredService<MemoryServerless>();

            var filters = chatApplicationQuery.Result.WikiIds
                .Select(chatApplication => new MemoryFilter().ByTag("wikiId", chatApplication.ToString())).ToList();

            var result = await memoryServerless.SearchAsync(input.Content, "wiki", filters: filters, limit: 3,
                minRelevance: chatApplicationQuery.Result.Relevancy);

            var fileIds = new List<long>();

            result.Results.ForEach(x =>
            {
                // 获取fileId
                var fileId = x.Partitions.Select(x => x.Tags.FirstOrDefault(x => x.Key == "fileId"))
                    .FirstOrDefault(x => !x.Value.IsNullOrEmpty())
                    .Value.FirstOrDefault();

                if (!fileId.IsNullOrWhiteSpace() && long.TryParse(fileId, out var id))
                {
                    fileIds.Add(id);
                }

                prompt += string.Join(Environment.NewLine, x.Partitions.Select(x => x.Text));
            });

            if (result.Results.Count == 0 &&
                !string.IsNullOrWhiteSpace(chatApplicationQuery.Result.NoReplyFoundTemplate))
            {
                yield return new CompletionsDto()
                {
                    Content = chatApplicationQuery.Result.NoReplyFoundTemplate
                };
                yield break;
            }

            var tokens = TokenHelper.GetGptEncoding().Encode(prompt);

            prompt = TokenHelper.GetGptEncoding()
                .Decode(tokens.Take(chatApplicationQuery.Result.MaxResponseToken).ToList());

            input.Content = chatApplicationQuery.Result.Template.Replace("{{quote}}", prompt)
                .Replace("{{question}}", input.Content);

            if (fileIds.Count > 0 && chatApplicationQuery.Result.ShowSourceFile)
            {
                var fileQuery = new StorageInfosQuery(fileIds);

                await EventBus.PublishAsync(fileQuery);

                sourceFile.AddRange(fileQuery.Result);
            }
        }

        var chatHistory = new ChatHistory();

        if (!chatApplicationQuery.Result.Prompt.IsNullOrWhiteSpace())
        {
            chatHistory.AddSystemMessage(chatApplicationQuery.Result.Prompt);
        }

        // TODO: 后期可修改为可配置
        var historyQuery = new ChatDialogHistoryQuery(input.ChatDialogId, 1, 3);

        await EventBus.PublishAsync(historyQuery);

        foreach (var message in historyQuery.Result.Result)
        {
            if (message.Current)
            {
                chatHistory.AddUserMessage(message.Content);
            }
            else
            {
                chatHistory.AddAssistantMessage(message.Content);
            }
        }

        chatHistory.AddUserMessage(input.Content);

        if (chatShareInfoQuery.Result.AvailableToken > 0)
        {
            var userToken =
                memoryCache.GetOrCreate(chatShareInfoQuery.Id, _ => chatShareInfoQuery.Result.AvailableToken);

            if (userToken < 0)
            {
                throw new UserFriendlyException("token已被消耗完成");
            }

            var token = chatHistory.Sum(history => TokenHelper.ComputeToken(history.Content ?? string.Empty));

            if (token > userToken)
            {
                throw new UserFriendlyException("token不足");
            }

            // 计算token
            userToken -= token;

            memoryCache.Remove(chatShareInfoQuery.Id);

            memoryCache.CreateEntry(chatShareInfoQuery.Id).Value = userToken;
        }

        var chatStream = wikiMemoryService.CreateOpenAIChatCompletionService(chatApplicationQuery.Result.ChatModel);

        await foreach (var item in chatStream.GetStreamingChatMessageContentsAsync(chatHistory))
        {
            yield return new CompletionsDto()
            {
                Content = item.Content ?? string.Empty,
            };
            await Task.Delay(1);
        }

        if (sourceFile.Count > 0)
        {
            yield return new CompletionsDto()
            {
                SourceFile = sourceFile.Select(x => new SourceFileDto()
                {
                    Name = x.Name,
                    FilePath = x.Path,
                    FileId = x.Id.ToString()
                }).ToList()
            };
        }
    }

    public async Task CreateChatDialogHistoryAsync(CreateChatDialogHistoryInput input)
    {
        var command = new CreateChatDialogHistoryCommand(input);

        await EventBus.PublishAsync(command);
    }

    public async Task<PaginatedListBase<ChatDialogHistoryDto>> GetChatDialogHistoryAsync(string chatDialogId, int page,
        int pageSize)
    {
        var query = new ChatDialogHistoryQuery(chatDialogId, page, pageSize);

        await EventBus.PublishAsync(query);

        return query.Result;
    }

    public async Task RemoveDialogHistoryAsync(string id)
    {
        var command = new RemoveChatDialogHistoryCommand(id);

        await EventBus.PublishAsync(command);
    }

    [Authorize]
    public async Task CreateShareAsync(CreateChatShareInput input)
    {
        var command = new CreateChatShareCommand(input);

        await EventBus.PublishAsync(command);
    }

    [Authorize]
    public async Task<PaginatedListBase<ChatShareDto>> GetChatShareListAsync(string chatApplicationId, int page,
        int pageSize)
    {
        var query = new ChatShareQuery(chatApplicationId, page, pageSize);

        await EventBus.PublishAsync(query);

        return query.Result;
    }

    [Authorize]
    public async Task RemoveDialogAsync(string id)
    {
        var command = new RemoveChatDialogCommand(id);

        await EventBus.PublishAsync(command);
    }

    [Authorize]
    public async Task UpdateDialogAsync(ChatDialogDto input)
    {
        var command = new UpdateChatDialogCommand(input);

        await EventBus.PublishAsync(command);
    }

    public async Task RemoveShareDialogAsync(string chatId, string id)
    {
        var command = new RemoveShareDialogCommand(chatId, id);
        await EventBus.PublishAsync(command);
    }

    public async Task UpdateShareDialogAsync(ChatDialogDto input)
    {
        var command = new UpdateShareChatDialogCommand(input);

        await EventBus.PublishAsync(command);
    }

    [Authorize]
    public async Task<PaginatedListBase<ChatDialogDto>> GetSessionLogDialogAsync(string chatApplicationId, int page, int pageSize)
    {
        var query = new GetSessionLogDialogQuery(chatApplicationId, page, pageSize);

        await EventBus.PublishAsync(query);

        return query.Result;
    }
}
