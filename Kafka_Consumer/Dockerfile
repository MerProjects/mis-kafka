FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Kafka_Consumer/Kafka_Consumer.csproj Kafka_Consumer/
RUN dotnet restore Kafka_Consumer/Kafka_Consumer.csproj
COPY . .
RUN dotnet publish Kafka_Consumer/Kafka_Consumer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet","Kafka_Consumer.dll"]