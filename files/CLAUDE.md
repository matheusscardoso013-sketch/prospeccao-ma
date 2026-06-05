# CLAUDE.md — Regras do projeto "Originação de Deals para M&A"

Estas são instruções permanentes. A especificação completa está em `especificacao.md` — leia-a antes de qualquer implementação. Este arquivo resume o que nunca deve ser violado.

## Contexto

Plataforma online e multiusuário que mapeia, classifica e prioriza empresas-alvo (targets) para M&A, com funil kanban. Usuária: uma boutique de M&A / contabilidade brasileira. Idioma do produto e do código (comentários, mensagens): **português**.

## Restrições inegociáveis

1. **Custo recorrente zero.** Toda ferramenta e fonte de dados deve ser gratuita. Não introduza serviços pagos sem me perguntar antes.
2. **IA roda localmente via Ollama.** Nunca use APIs de IA pagas. A classificação fica atrás da interface `IClassificadorIA`; a implementação default é `OllamaClassificador` (`localhost:11434`, `format: json`).
3. **Plataforma online, multiusuário, com login.** ASP.NET Core Identity, papéis `Socio` e `Analista`, HTTPS, todas as rotas autenticadas. Base de dados compartilhada pela equipe.
4. **Rigor de fonte.** Todo dado guarda origem e data de coleta. Estimativas marcadas com `IndicadorEstimado` e exibidas com "~". Nunca invente dados; se algo for estimado, sinalize.
5. **LGPD.** Apenas dados públicos.

## Stack (não trocar sem confirmar)

- ASP.NET Core MVC (.NET 8) + Entity Framework Core — modular monolith.
- SQL Server (Express/LocalDB em dev).
- Ollama (`qwen2.5:7b` default) para classificação.
- Frontend: HTML/CSS/JS, SortableJS (kanban), Tabulator.js (tabela).
- BackgroundService nativo para o ciclo diário.

## Disciplina de execução

- **Uma fase por vez.** Roadmap na seção 6 da especificação. NÃO inicie a Fase N+1 antes de a Fase N cumprir seu critério de aceitação. Comece pela **Fase 1**.
- **Mostre o plano antes de codar.** Antes de criar arquivos numa fase nova, descreva a estrutura de pastas e a ordem das tarefas, e espere meu OK.
- **Ciclo de ingestão idempotente.** Rodar duas vezes no mesmo dia não pode duplicar empresas nem gatilhos (CNPJ único + `HashEvento`).
- **Parser de IA defensivo.** Saída malformada do modelo vira item "não classificado" com texto bruto preservado — nunca derrube o ciclo.
- **Segurança.** Ollama nunca exposto à internet. Senhas só via Identity (hash). HTTPS em produção.

## Convenções de código

- Nomes de entidades e campos exatamente como na seção 4 da especificação.
- Pesos do scoring em `appsettings.json` (seção `Scoring`), nunca hardcoded.
- Migrations do EF Core versionadas; aplicar os índices obrigatórios da seção 4.

## Quando estiver em dúvida

Pergunte antes de: introduzir qualquer dependência paga, expor o Ollama, mudar a stack, ou avançar de fase. Em decisões reversíveis e triviais, siga e me informe o que fez.
