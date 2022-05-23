#!/bin/bash

dotnet publish -c Release -o bin/publish

docker build . -t msel/api --no-cache

docker-compose up -d