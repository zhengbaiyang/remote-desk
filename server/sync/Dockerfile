FROM python:3.12-alpine

RUN addgroup -g 10001 rdsync && adduser -D -u 10001 -G rdsync rdsync && mkdir -p /data && chown -R 10001:10001 /data

WORKDIR /app
COPY security.py storage.py app.py /app/

USER rdsync
EXPOSE 11020

CMD ["python", "-u", "/app/app.py"]
