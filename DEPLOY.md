# Publicar online (grátis) — Render

O app já está pronto para a nuvem: tem `Dockerfile`, lê a porta da plataforma (`PORT`),
respeita o proxy (HTTPS/login) e aplica a migration sozinho no startup.

## Pré-requisito: código no GitHub

Render publica a partir de um repositório Git. Se ainda não estiver no GitHub:

```bash
git init
git add .
git commit -m "Prospeccao MA"
# crie um repo vazio no github.com e then:
git remote add origin https://github.com/SEU_USUARIO/prospeccao-ma.git
git push -u origin main
```

> O `.gitignore`/`.dockerignore` já excluem `bin/`, `obj/`, o banco local e as amostras.
> **Nunca** suba a chave do Gemini nem a string do Neon — elas vão como variáveis de ambiente no Render (passo 3).

## Passos no Render

1. Acesse https://render.com e faça **Sign up** (Continue with GitHub).
2. **New + → Web Service** → conecte o repositório `prospeccao-ma`.
3. Configurações:
   - **Runtime:** Docker (ele detecta o `Dockerfile` automaticamente)
   - **Instance Type:** Free
   - **Region:** a mais próxima
4. Em **Environment → Add Environment Variable**, adicione (atenção ao **duplo sublinhado**):

   | Key | Value |
   |---|---|
   | `ConnectionStrings__Neon` | sua string `postgresql://...` do Neon |
   | `Gemini__ApiKey` | sua chave do Gemini |
   | `Gemini__Modelo` | `gemini-2.5-flash` |
   | `Admin__Email` | seu e-mail de login |
   | `Admin__Senha` | uma senha forte |
   | `Prospeccao__HoraExecucao` | `12` |

5. **Create Web Service**. O Render builda o Docker e sobe. Na primeira vez, a migration
   é aplicada no Neon e o usuário admin é criado automaticamente.
6. Acesse a URL pública (`https://prospeccao-ma.onrender.com`) e faça login.

## Observações do free tier

- **Hibernação:** o serviço grátis do Render dorme após ~15 min sem acesso. Se ele estiver
  dormindo às 12h, o job pode não disparar. Solução grátis: um cron externo
  (ex.: cron-job.org) que faz um GET na URL alguns minutos antes das 12h para "acordar" o app.
- **Neon:** também hiberna e religa sozinho na primeira query (centenas de ms). Tudo bem.
- **Storage do Neon (~0,5 GB):** importe só o recorte de CNAEs/UFs em uso (é o que o
  importador da Receita faz). Não carregue a base inteira.

## Importar o recorte real da Receita (em produção)

Os arquivos oficiais (dezenas de GB) ficam em https://dadosabertos.rfb.gov.br/CNPJ/.
Baixe e descompacte os CSVs de **Empresas**, **Estabelecimentos** e **Municípios**
numa pasta e rode o comando de console apontando para ela:

```bash
dotnet ProspeccaoMA.Web.dll importar-receita "/caminho/dados" --cnaes 4646,2110 --ufs SP,MG --gravar
```

Sem `--gravar` é dry-run (só conta). Como é pesado, rode localmente/numa máquina com os
arquivos e a mesma `ConnectionStrings__Neon` — ele grava direto no Neon.
