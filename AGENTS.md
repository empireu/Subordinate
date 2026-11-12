# KnowledgeSystem

KnowledgeSystem is a project involving AI agents and various other utilities, that are consumed via a discord bot. Used to help us on our Space Engineers' faction discord server.

## Code Style

We are working with C#. There is a strict code style you must follow.

1. **Always use braces, do not use whitespace-based formatting, and do not write same-line statements.**

Bad:

```cs
if(condition) return;
if(condition) { DoX(); }

if(condition)
    return;

if(condition)
    FunctionA();
else
    FunctionB();
```

Good:

```cs
if(condition) 
{ 
    return;
}

if(condition) 
{
    FunctionA();
}
else 
{ 
    FunctionB();
} 
```

2. **Do NOT use any special symbols, such as emdashes, arrows, or other unicode characters. Use the standard latin alphabet, plus numbers and the common symbols present on a standard QWERTY keyboard.**
3. **Do NOT write any comments; write comments only if asked by the USER. NEVER use separators or any such symbols in comments. The comment must always be attached to something (a class, method, statement). Do NOT break a single sentence over multiple lines. Keep the comment very short so you don't need to.**
   Bad comment:
   
   ```cs
   //=============================
   // Authentication Services 
   //=============================
   ```

AddAuthenticationServices();

```
```cs
// We don't pass the token, so the caller has to 
// wait for the process to exit — it doesn't make
// sense to blah blah...
await process.WaitForExitAsync();
```

Good comment:

```cs
process.Kill(true);

// We don't pass the token, so the caller has to wait for the process to exit:
// ReSharper disable once MethodSupportsCancellation
await process.WaitForExitAsync();
```

4. **Do not write inefficient code. Whenever you need to write an algorithm that you want to keep simple by e.g. brute-forcing something, always ask the user if that's fine. The database or dataset can scale explosively, so you cannot actually infer the impact of a bad piece of code.**
5. **Always think of the possible errors. A fatal error should not crash the driver process; always make sure fatal errors show up as friendly messages to the discord bridge/LLM (if you are writing an agent tool), and no sensitive data is leaked.** 

## Project Structure

- `KnowledgeSystem` — driver program, that loads plugins and their required libraries at runtime, and sets up various services such as the Discord integration and shared AI services. Uses the standard dotnet Hosting and DI architecture. Plugins build against the many libraries included in the project; `KnowledgeSystem` loads them up and sets up their services.
- `KnowledgeSystem.Ai` — raw LLM API library. Provides client implementations for OpenAI-style completions endpoints, embedding endpoints and some other miscellaneous types of endpoints.
- `KnowledgeSystem.Agents` — AI agent library. Provides the tools needed to build AI loops that call tools and subagents.
- `KnowledgeSystem.Events` — reflection-based event system library, used for many things.
- `KnowledgeSystem.Plugins.*` — The plugins and some shared utilities. All of them auto-deploy to a local test runtime via build events. These include:
  - `KnowledgeSystem.Plugins.Library` — Discord message bridge, and some useful agentic tool implementations.\
  - `KnowledgeSystem.Plugins.Wiki` — The biggest plugin. Implements a markdown RAG with advanced search tools (keyword and semantic) and a code RAG with ripgrep integration, .NET decomplier integration, read-only GIT integration.
  - `KnowledgeSystem.Plugins.OreDb` — Ore database for Space Engineers. The ore-generation is reverse-engineered via an external program, millions of asteroids are pre-generated, stored, then can be queried and marked as mined.

## Other Rules

**You are not allowed to touch GIT in any way!**
