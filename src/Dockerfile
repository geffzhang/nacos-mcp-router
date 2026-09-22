FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY NacosMcpRouter/NacosMcpRouter.csproj NacosMcpRouter/
RUN dotnet restore NacosMcpRouter/NacosMcpRouter.csproj
COPY . .
RUN dotnet publish NacosMcpRouter -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates bash \
    && rm -rf /var/lib/apt/lists/*
COPY scripts/download-model.sh /usr/local/bin/download-model.sh
RUN chmod +x /usr/local/bin/download-model.sh
ENV EMBEDDING_MODEL_DIR=/app/models/all-MiniLM-L6-v2
ENV SONNETDB_DATA_DIR=/app/.data/mcp-router
ENV TRANSPORT_TYPE=streamable_http
ENV NACOS_ADDR=127.0.0.1:8848
ENV NACOS_USERNAME=nacos
ENTRYPOINT ["/bin/bash", "-c", "if [ ! -f \"$EMBEDDING_MODEL_DIR/model.onnx\" ]; then download-model.sh \"$(dirname \"$EMBEDDING_MODEL_DIR\")\" || exit 1; fi; exec dotnet NacosMcpRouter.dll"]
EXPOSE 8000