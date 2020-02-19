using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using EP.Ner;
using EP.Ner.Core;
using EP.Morph;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Linq;
using EP.Ner.Decree;
using EP.Ner.Uri;
using EP.Ner.Org;
using EP.Ner.Person;
using EP.Demo.Core;
using PravoRu.Common.Extensions;
using PravoRu.DataLake.Arbitr.CaseCard.Api.Client.v1;

namespace Demo
{
	public class GlobalState
	{
		public static String File { get; set; }
	}

	class Program
	{
		static void Main(string[] args)
		{
			Stopwatch sw = Stopwatch.StartNew();

			// инициализация - необходимо проводить один раз до обработки текстов
			Console.Write("Initializing ... ");

			ProcessorService.Initialize(MorphLang.RU | MorphLang.EN);
			// инициализируются все используемые анализаторы
			MyAnalyzer.Initialize();

			Console.WriteLine("OK (by {0} ms), version {1}", (int) sw.ElapsedMilliseconds, ProcessorService.Version);

			AnalysisResult ar = null;

			if (args.Length > 0)
			{
				if (args[0] == "csv")
				{
					ClientApiSettings settings = new ClientApiSettings();
					foreach (var file in Directory.GetFiles("Texts", "*.csv"))
					{
						using (var sr = new StreamReader(file))
						{
							var i = 1;
							var line = sr.ReadLine();
							while (line != null)
							{
								var data = line.Split(';', ' ', ';');
								if (data.Length < 3)
								{
									Console.WriteLine("Ошибка формата csv. \r\n Формат\r\n Name;CaseId;DocumentId");
								}

								GlobalState.File = i + "_" + MakeValidFileName(data[0]) + ".txt";

								var client = new PravoRu.DataLake.Arbitr.CaseCard.Api.Client.v1.FileClient(settings,
									new HttpClientFactory());
								DocumentPlainText response = null;
								try
								{
									response = client.GetDocumentTextAsync(new DocumentFileRequest()
									{
										CaseId = Guid.Parse(data[1]),
										IsBase64 = false,
										DocumentId = Guid.Parse(data[2])
									}).GetAwaiter().GetResult();
								}
								catch (Exception e)
								{
									Console.WriteLine(data[0] + "\t" + e.Message);
								}

								if (response == null)
								{
									line = sr.ReadLine();
									continue;
								}


                                File.WriteAllText(Path.Combine("Results", "Original_" + GlobalState.File), response.HtmlText);

                                // создаём экземпляр обычного процессора
                                using (Processor proc = ProcessorService.CreateSpecificProcessor(nameof(MyAnalyzer)))
                                {
                                    // анализируем текст
                                    ar = proc.Process(new SourceOfAnalysis(response.HtmlText));
                                    try
                                    {
                                        PostExecute(ar);
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                    }
                                }

								Console.WriteLine("Обработан файл " + GlobalState.File);
								line = sr.ReadLine();
								i++;
							}
						}
					}
				}

				if (args[0] == "txt")
				{
					foreach (var file in Directory.GetFiles("Texts", "*.txt"))
					{
						Console.WriteLine($"{file}------------------------------------");
						string txt = File.ReadAllText(file);
						GlobalState.File = new FileInfo(file).Name;
						// создаём экземпляр обычного процессора
						using (Processor proc = ProcessorService.CreateSpecificProcessor(nameof(MyAnalyzer)))
						{
							// анализируем текст
							ar = proc.Process(new SourceOfAnalysis(txt));
							try
							{
								PostExecute(ar);
							}
							catch (Exception e)
							{
								Console.WriteLine(e);
							}
						}
					}
				}
			}


			sw.Stop();
			Console.WriteLine("Over!(by {0} ms), version {1}", (int) sw.ElapsedMilliseconds, ProcessorService.Version);
		}

		private static void PostExecute(AnalysisResult ar)
		{
			List<(object Id, object Number, object RegNumber, object SignDate, object PublishDate)> contractList =
				new List<(object Id, object Number, object RegNumber, object SignDate, object PublishDate)>();

			var contractNumberList = new List<string>();

			foreach (var referent in ar.Entities.OfType<MyReferent>())
			{
				if (referent.Contracts?.Count > 0)
				{
					var participants = referent.Participants.Where(x => !x.Inn.NullOrEmpty() || !x.Ogrn.NullOrEmpty())
						.ToList();
					if (participants.Count == 0)
					{
						Console.WriteLine("Нет участников дела");
					}
					else if (participants.Count > 1)
					{
						foreach (var participant in participants)
						{
							var otherParticipants = participants
								.Where(x => x.Ogrn != participant.Ogrn || x.Inn != participant.Inn).ToList();
							contractList.AddRange(SearchContract(participant, otherParticipants, referent.Contracts));
						}
					}
					else
					{
						contractList.AddRange(SearchContract(participants.First(), referent.Contracts));
					}
				}

				contractNumberList.AddRange(referent.Contracts.Select(x => x.Number));
			}

			using (var streamWriter = new StreamWriter(Path.Combine("Results", GlobalState.File), true))
			{
				streamWriter.WriteLine();
				streamWriter.WriteLine("Могли бы привязаться к следующим ID контрактов в GosZakupki между участниками");
				streamWriter.WriteLine();
				streamWriter.WriteLine("Id, Number, RegNumber, SignDate/ContractDate, PublishDate");
				foreach (var contract in contractList)
				{
					streamWriter.WriteLine($"{contract}");
				}

				streamWriter.WriteLine();
				streamWriter.WriteLine("Контракт, который точно подходит нам");
				streamWriter.WriteLine();

				foreach (var contract in contractList.Where(x =>
					contractNumberList.Contains(x.Number) || contractNumberList.Contains(x.RegNumber)))
				{
					streamWriter.WriteLine($"{contract}");
				}
			}
		}

		private static IEnumerable<(object Id, object Number, object RegNumber, object SignDate, object PublishDate)>
			SearchContract(
				Participant participant, IEnumerable<Participant> otherParticipants, IEnumerable<Contract> contracts)
		{
			using (var sqlConnection =
				new SqlConnection(
					@"Server=DB_DL; Database=GosZakupki_new; Integrated Security=SSPI; MultipleActiveResultSets=True;"))
			{
				sqlConnection.Open();
				Console.WriteLine("Первый запрос");
				var command = new SqlCommand($@"
														SELECT COUNT(1)  from Contract c with(nolock)
														inner join Supplier s with(nolock) on c.Id = s.ContractId
														where c.IsLastVersion = 1 AND CustomerInn = '{participant.Inn}' and (s.Inn IN ({string.Join(',', otherParticipants.Select(x => "'" + x.Inn + "'"))}) 
														or s.Ogrn in ({string.Join(',', otherParticipants.Select(x => "'" + x.Ogrn + "'"))}))
													", sqlConnection);
				Console.WriteLine(command.CommandText);
				var reader = command.ExecuteReader();
				reader.Read();
				var count = (int) reader[0];
				if (count == 0)
				{
					Console.WriteLine("Ничего не вернулось");
					//Нифига не нашли
				}
				else if (count == 1) //Возможно вторая удача за день
				{
					Console.WriteLine("Вернулся один контракт");
					command = new SqlCommand($@"
														SELECT c.Id, c.Number, c.RegNumber, c.SignDate, c.PublishDate  from Contract c with(nolock)
														inner join Supplier s with(nolock) on c.Id = s.ContractId
														where c.IsLastVersion = 1 AND CustomerInn = '{participant.Inn}' and (s.Inn IN ({string.Join(',', otherParticipants.Select(x => "'" + x.Inn + "'"))}) 
														or s.Ogrn in ({string.Join(',', otherParticipants.Select(x => "'" + x.Ogrn + "'"))}))
													", sqlConnection);
					Console.WriteLine(command.CommandText);
					reader = command.ExecuteReader();
					reader.Read();
					yield return (reader[0], reader[1], reader[2], reader[3], reader[4]);
				}
				else //Попробуем дофильтровать по дате и по номеру
				{
					Console.WriteLine($"Вернулось {count} контрактов");
					Console.WriteLine($"Попробуем дофильтровать по дате и по номеру");
					command = new SqlCommand($@"
														SELECT COUNT(1)  from Contract c with(nolock)
														inner join Supplier s with(nolock) on c.Id = s.ContractId
														where c.IsLastVersion = 1 AND CustomerInn = '{participant.Inn}' and (s.Inn IN ({string.Join(',', otherParticipants.Select(x => "'" + x.Inn + "'"))}) 
														or s.Ogrn in ({string.Join(',', otherParticipants.Select(x => "'" + x.Ogrn + "'"))})) 
														and (ISNULL(c.ContractDate, c.SignDate) in ({string.Join(',', contracts.SelectMany(x => x.Dates.Select(a => '\'' + a.Value.ToString("yyyy-MM-dd") + '\'')))})
														or c.Number in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))})
														or c.RegNumber in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))}))
													", sqlConnection);
					Console.WriteLine(command.CommandText);
					reader = command.ExecuteReader();
					reader.Read();
					count = (int) reader[0];
					Console.WriteLine($"Вернулось {count} контрактов");
					if (count > 0)
					{
						command = new SqlCommand($@"
														SELECT c.Id, c.Number, c.RegNumber, c.SignDate, c.PublishDate  from Contract c with(nolock)
														inner join Supplier s with(nolock) on c.Id = s.ContractId
														where c.IsLastVersion = 1 AND CustomerInn = '{participant.Inn}' and (s.Inn IN ({string.Join(',', otherParticipants.Select(x => "'" + x.Inn + "'"))}) 
														or s.Ogrn in ({string.Join(',', otherParticipants.Select(x => "'" + x.Ogrn + "'"))})) 
														and (ISNULL(c.ContractDate, c.SignDate) in ({string.Join(',', contracts.SelectMany(x => x.Dates.Select(a => '\'' + a.Value.ToString("yyyy-MM-dd") + '\'')))})
														or c.Number in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))})
														or c.RegNumber in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))}))
													", sqlConnection);
						Console.WriteLine(command.CommandText);
						reader = command.ExecuteReader();
						while (reader.Read())
						{
							yield return (reader[0], reader[1], reader[2], reader[3], reader[4]);
						}
					}
				}
			}
		}

		private static IEnumerable<(object Id, object Number, object RegNumber, object SignDate, object PublishDate)>
			SearchContract(
				Participant participant, IEnumerable<Contract> contracts)
		{
			using (var sqlConnection =
				new SqlConnection(
					@"Server=DB_DL; Database=GosZakupki_new; Integrated Security=SSPI; MultipleActiveResultSets=True;"))
			{
				sqlConnection.Open();
				Console.WriteLine("Первый запрос");
				//Ищем так: Участник - заказчик при этом дата контракта это одни из выделенных дат или номер контракта один из выделенных
				var command = new SqlCommand($@"
														SELECT COUNT(1)  from Contract c with(nolock)
														inner join Customer cust with(nolock) ON c.CustomerInn = cust.Inn
														where c.IsLastVersion = 1 AND (cust.Inn = '{participant.Inn}' or cust.Ogrn='{participant.Ogrn}') 
														and (ISNULL(c.ContractDate, c.SignDate) in ({string.Join(',', contracts.SelectMany(x => x.Dates.Select(a => '\'' + a.Value.ToString("yyyy-MM-dd") + '\'')))})
														or c.Number in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))})
														or c.RegNumber in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))}))
													", sqlConnection);
				Console.WriteLine(command.CommandText);
				var reader = command.ExecuteReader();
				reader.Read();
				var count = (int) reader[0];
				if (count == 0)
				{
					Console.WriteLine("Ничего не вернулось в качестве заказчика");
					command = new SqlCommand($@"
														SELECT c.Id, c.Number, c.RegNumber, c.SignDate, c.PublishDate  from Contract c with(nolock)
														inner join Supplier s with(nolock) on c.Id = s.ContractId
														where c.IsLastVersion = 1 AND (s.Inn = '{participant.Inn}' or s.Ogrn = '{participant.Ogrn}')
														and (ISNULL(c.ContractDate, c.SignDate) in ({string.Join(',', contracts.SelectMany(x => x.Dates.Select(a => '\'' + a.Value.ToString("yyyy-MM-dd") + '\'')))})
														or c.Number in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))})
														or c.RegNumber in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))}))
													", sqlConnection);
					Console.WriteLine(command.CommandText);
					reader = command.ExecuteReader();
					while (reader.Read())
					{
						yield return (reader[0], reader[1], reader[2], reader[3], reader[4]);
					}
				}
				else if (count > 0) 
				{
					Console.WriteLine($"Вернулось {count} контрактов");
					command = new SqlCommand($@"
														SELECT c.Id, c.Number, c.RegNumber, c.SignDate, c.PublishDate  from Contract c with(nolock)
														where c.IsLastVersion = 1 AND CustomerInn = '{participant.Inn}' 
														and (ISNULL(c.ContractDate, c.SignDate) in ({string.Join(',', contracts.SelectMany(x => x.Dates.Select(a => '\'' + a.Value.ToString("yyyy-MM-dd") + '\'')))})
														or c.Number in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))})
														or c.RegNumber in ({string.Join(',', contracts.Select(a => '\'' + a.Number + '\''))}))
													", sqlConnection);
					Console.WriteLine(command.CommandText);
					reader = command.ExecuteReader();
					while (reader.Read())
					{
						yield return (reader[0], reader[1], reader[2], reader[3], reader[4]);
					}
				}
			}
		}

		private static string MakeValidFileName(string name)
		{
			string invalidChars =
				System.Text.RegularExpressions.Regex.Escape(new string(System.IO.Path.GetInvalidFileNameChars()));
			string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

			return System.Text.RegularExpressions.Regex.Replace(name, invalidRegStr, "_");
		}
	}
}