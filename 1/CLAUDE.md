# CLAUDE.md — Contexto do Projeto

Plataforma web online de prospecção de leads de M&A sell-side, com qualificação por
IA e dashboard alimentado por job diário às 12h.

A especificação completa está em `Especificacao_Agente_MA.md`. Leia-a antes de
qualquer tarefa estrutural.

## Stack (não substituir sem perguntar)

- ASP.NET Core (MVC ou API + front), EF Core
- **PostgreSQL no Neon** (nuvem) via provider **Npgsql.EntityFrameworkCore.PostgreSQL**
- API da Anthropic (Claude) **apenas** para qualificação / redação de racional
- Exportação Excel com **ClosedXML**
- Agendamento via **BackgroundService** (disparo às 12h)
- Hospedagem do app: Render / Railway (free tier)

## Regra de ouro (inegociável)

**Somente dados reais. O Claude NUNCA inventa empresas, CNPJs ou números.**
O Claude apenas pontua e descreve candidatos reais vindos do Postgres (recorte da
base pública da Receita Federal). Todo valor estimado recebe prefixo `~` e campo
`Fonte`.

## Fonte de dados

- **Prospecção:** recorte da base pública de CNPJs da Receita importado no Postgres
  (Neon), consultado por CNAE, UF, capital social, situação, porte. Importar só os
  CNAEs/UFs em uso — o free tier do Neon tem ~0,5 GB.
- **Enriquecimento pontual:** BrasilAPI / ReceitaWS por CNPJ, com rate limit.
- BrasilAPI/ReceitaWS NÃO fazem busca reversa por setor — não usar para descobrir
  empresas, só para enriquecer leads já selecionados.

## Convenções

- Idioma do código/comentários: português.
- Banco no Neon desde o início — **não usar banco local nem SQL Server**.
- Connection string do Neon e chave da API: **nunca** no código. Usar variáveis de
  ambiente / user-secrets.
- Respostas do Claude no job: **JSON estrito** `{ "score": 0-100, "racional": "..." }`,
  com parsing protegido por try/catch.
- APIs externas: retry com backoff, respeitar rate limit.

## Fluxo de trabalho esperado

1. Antes de codar tarefas estruturais, apresentar o plano e aguardar OK.
2. Após cada etapa: rodar `dotnet build` e corrigir erros antes de prosseguir.
3. Migrations EF Core devem aplicar no Neon sem erro.
4. Não adicionar pacotes além dos listados sem justificar e perguntar.

## Checklist antes de concluir uma etapa

- [ ] `dotnet build` sem erros
- [ ] Migration aplica no Neon sem erro
- [ ] Job roda end-to-end gravando ao menos 1 lead real de teste
- [ ] Resposta do Claude faz parse de JSON sem exceção
- [ ] Export gera `.xlsx` abrível no Excel
- [ ] Tamanho do banco dentro do free tier do Neon
