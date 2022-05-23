#
#multi-stage target: dev
#
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS dev

ENV ASPNETCORE_URLS=http://0.0.0.0:4302 \
    ASPNETCORE_ENVIRONMENT=DEVELOPMENT

# Must be copied above both msel and stackstorm. Will not accept ../stackstorm.api for copying.
COPY . /app
WORKDIR /app/Blueprint.Api

RUN dotnet publish -c Release -o /app/dist

CMD ["dotnet", "run"]

#
#multi-stage target: prod
#
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS prod
COPY --from=dev /app/dist /app

WORKDIR /app
ENV ASPNETCORE_URLS=http://*:80
EXPOSE 80
CMD [ "dotnet", "Blueprint.Api.dll" ]

RUN apt-get update && \
	apt-get install -y jq
