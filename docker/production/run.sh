#!/bin/bash

export MYSQL_ROOT_PASSWORD="default-root-password"
export MYSQL_PASSWORD="default-mysql-password"
export MINIO_ACCESS_KEY="minioadmin"
export MINIO_SECRET_KEY="miniopass"
export ADMIN_PASS="redisadminpass"
export REGISTRY_ADMIN_MAIL="test@test.it"
export REGISTRY_ADMIN_PASSWORD="password"
export REGISTRY_SECRET="e7er2yjacmbqjxsmf6h3rtrh7t6wjhef7bkv6kauv3wng3jb3t5hx7jtjry5z2ydd6utbufgq6jar2v3cvexhcescgzacfwvg5kqfa3gx3ppzchdtwcakx5hr3s6485z"

envsubst < appsettings-template.json > appsettings.json
envsubst < initialize-template.sql > initialize.sql

docker-compose up --build -d
