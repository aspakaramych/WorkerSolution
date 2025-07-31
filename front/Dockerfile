FROM node:20-slim

WORKDIR /app

COPY package.json package.json
COPY package-lock.json package-lock.json

RUN npm install --ci

COPY . .

EXPOSE 14000

CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]