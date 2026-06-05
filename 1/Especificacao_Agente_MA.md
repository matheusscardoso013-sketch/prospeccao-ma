# Especificação Técnica — Agente de IA para Prospecção de Leads M&A (Sell-Side)

> Documento de referência para execução em Claude Code.
> Stack: ASP.NET Core · PostgreSQL (Neon) · API Anthropic · Dashboard Web.

---

## 1. Visão Geral e Objetivo

Plataforma web **online** que prospecta automaticamente empresas-alvo **reais** para
operações de M&A sell-side, qualifica cada candidato por sinergia setorial usando a
API da Anthropic (Claude), e apresenta os resultados em um dashboard alimentado
diariamente por um job agendado (12h). Usuários autenticados configuram setores de
interesse, visualizam os leads pontuados e exportam os dados para Excel.

### Princípio inegociável: somente dados reais

Toda empresa exibida deve originar de fonte real e rastreável. **O Claude NUNCA
gera, completa ou inventa empresas, CNPJs ou números.** O papel do Claude é
qualificar e redigir o racional sobre candidatos que o banco de dados já forneceu.
Qualquer valor estimado (ex.: porte/faturamento inferido) deve ser marcado com o
prefixo `~` e ter sua origem documentada no campo `Fonte`.

---

## 2. Arquitetura

O fluxo separa claramente a fonte de dados (real), o motor de qualificação (Claude)
e a camada de apresentação (web). Tudo na nuvem desde o início.

| Camada | Tecnologia | Responsabilidade |
|---|---|---|
| Fonte de prospecção | Recorte da base pública da Receita Federal importado no Postgres (Neon) | Universo filtrável de CNPJs por CNAE, UF, capital social, situação, porte |
| Enriquecimento | BrasilAPI / ReceitaWS (consulta pontual por CNPJ) | Validar situação atual e obter contato de leads já selecionados, respeitando rate limit |
| Qualificação | API Anthropic (Claude) | Pontuar sinergia, priorizar e redigir racional textual de cada lead |
| Persistência | PostgreSQL no Neon + EF Core (provider Npgsql) | Armazenar leads, scores, configurações de usuário e histórico |
| Aplicação | ASP.NET Core (MVC ou API + front) | Autenticação, dashboard, exportação Excel |
| Agendamento | Hosted Service (BackgroundService) | Disparar a rotina de prospecção todo dia às 12h |
| Hospedagem do app | Render / Railway (free tier) | App online acessível por usuários |

### Banco: PostgreSQL no Neon (não SQL Server)

- Provider EF Core: **Npgsql.EntityFrameworkCore.PostgreSQL**.
- O banco vive no Neon desde o primeiro dia — **nada de banco local**.
- Neon faz scale-to-zero após inatividade e religa sozinho na próxima query
  (retomada na casa de centenas de ms). O job diário das 12h mantém o banco ativo.
- Connection string vem do painel do Neon e é lida de variável de ambiente.

### Por que um RECORTE da Receita, e não a base inteira

O free tier do Neon oferece ~0,5 GB de storage; a base completa de CNPJs da Receita
tem dezenas de GB e **não cabe**. Portanto importa-se apenas o recorte relevante:
os CNAEs e UFs que os clientes da boutique prospectam. Para uma boutique focada em
setores específicos, isso é mais adequado do que carregar o Brasil inteiro.

### Por que a base da Receita, e não só BrasilAPI

BrasilAPI e ReceitaWS consultam **um CNPJ específico de cada vez**; não fazem busca
reversa por setor/região. Prospecção exige descobrir empresas ainda desconhecidas,
o que só é possível filtrando um universo de CNPJs. As APIs de consulta entram
**depois**, apenas para enriquecer os leads escolhidos.

### Limitação conhecida a aceitar

- A base pública **não contém faturamento nem EBITDA reais** de empresas fechadas.
- Porte/faturamento são **estimados** a partir de capital social, CNAE e porte
  declarado — sempre marcados com `~`.
- Dados de contato dependem do que houver no cadastro; nem todo CNPJ traz
  telefone/e-mail.

---

## 3. Modelo de Dados (essencial)

| Tabela | Campos-chave | Observação |
|---|---|---|
| Usuarios | Id, Email, SenhaHash, CriadoEm | Autenticação da plataforma |
| ConfiguracoesProspeccao | Id, UsuarioId, Cnaes, Ufs, CapitalMin, CapitalMax, Ativo | Setores configuráveis por usuário |
| Leads | Id, Cnpj, RazaoSocial, Cnae, Uf, Municipio, CapitalSocial, Situacao, PorteEstimado, Contato | Empresa real vinda da Receita |
| LeadScores | Id, LeadId, ConfiguracaoId, Score, Racional, Fonte, GeradoEm | Saída do Claude; Fonte rastreia origem |
| ExecucoesJob | Id, IniciadoEm, FinalizadoEm, LeadsGerados, Status, Erro | Auditoria da rotina diária |

---

## 4. Rotina Diária de Prospecção (12h)

1. Para cada configuração ativa, consultar o Postgres filtrando o universo de
   CNPJs pelos CNAEs, UFs e faixa de capital social definidos pelo usuário.
2. Deduplicar contra leads já existentes para não repetir empresas.
3. Selecionar um lote (ex.: 50–200 candidatos) priorizando situação cadastral ativa.
4. Para cada candidato, montar um prompt com os **dados reais** e pedir ao Claude um
   score de sinergia (0–100) e um racional curto.
5. Persistir `Lead` + `LeadScore` com a `Fonte` preenchida
   ("Receita Federal — base pública").
6. Registrar a execução em `ExecucoesJob` (sucesso/erro, contagem).
7. Opcional: enriquecer os top-N via BrasilAPI respeitando rate limit.

### Regra de chamada ao Claude

O prompt enviado ao Claude deve conter **apenas dados reais** do candidato e instruir
explicitamente: *"Não invente informações. Avalie a sinergia somente com base nos
dados fornecidos. Se faltar dado, diga que falta."* A resposta deve vir em **JSON
estrito** (`{ "score": 0-100, "racional": "..." }`) para parsing seguro com
try/catch.

---

## 5. Dashboard e Exportação

- Login de usuário e área autenticada.
- Tela de configuração de setores (CNAE), UFs e faixa de capital.
- Listagem de leads com score, racional, situação e data de geração; filtros e
  ordenação por score.
- Botão "Exportar para Excel" gerando `.xlsx` (ClosedXML) com os leads filtrados.
- Indicador da última execução do job e total de leads do dia.

---

## 6. Hospedagem (online + gratuito)

| Componente | Opção | Nota |
|---|---|---|
| Banco | **Neon** (free tier) | Postgres gerenciado, scale-to-zero, religa sozinho |
| App ASP.NET Core | Render / Railway (free tier) | App online; free tiers podem hibernar |
| Job 12h | Hosted Service no app, ou cron externo | Se o tier hibernar, usar cron externo para acordar |
| API Claude | Pay-as-you-go (**não é grátis**) | Único custo real; mitigar com lote pequeno e cache |

**Atenção:** a API da Anthropic é **paga por uso**. "Grátis" aplica-se à hospedagem e
à fonte de dados, não ao Claude. Para conter custo: processar lotes pequenos, usar o
modelo mais econômico adequado e evitar reprocessar leads já pontuados.

**Limite de storage:** o free tier do Neon (~0,5 GB) exige manter o banco enxuto —
importar só o recorte de CNAEs/UFs em uso, não a base inteira.

---

## 7. Restrições Anti-Erro (para o agente)

- NUNCA gerar empresas, CNPJs ou números fictícios.
- Todo dado estimado recebe `~` e campo `Fonte` preenchido.
- Respostas do Claude no job devem ser JSON estrito e ter parsing com try/catch.
- Respeitar rate limit das APIs externas (retry com backoff).
- Rodar `dotnet build` e corrigir erros de compilação antes de concluir cada etapa.
- Manter o stack: ASP.NET Core / EF Core (Npgsql) / PostgreSQL. Não introduzir
  dependências novas sem necessidade nem reintroduzir SQL Server.
- Vigiar o tamanho do banco para não estourar o free tier do Neon.
