# ChatAssistantVSIX

A Visual Studio **ToolWindow VSIX extension** that provides a chat interface similar to GitHub Copilot Chat.  
It leverages a **locally deployed Retrieval-Augmented Generation (RAG) system** for interacting with codebases.

## Features

- ToolWindow integrated directly into Visual Studio  
- Chat with context-aware responses from your local codebase  
- Powered by a custom RAG setup for private, offline code assistance  
- Minimal dependencies; fully runs locally  

## Installation

1. Build the VSIX project in Visual Studio.  
2. Install the generated `.vsix` file.  
3. Open the **ChatAssistant ToolWindow** from the `View -> Other Windows` menu.  

## Usage

- Type queries or prompts in the chat window.  
- The extension will retrieve relevant context from your codebase and generate helpful responses.  

## Development

- C# ToolWindow VSIX project  
- .NET Framework / .NET Standard (as per project configuration)  
- Local RAG service is required to provide code-aware responses  

## License

[Not set]  
