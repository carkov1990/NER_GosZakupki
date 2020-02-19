using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Demo;
using EP.Morph;
using EP.Ner;
using EP.Ner.Core;
using EP.Ner.Date;
using EP.Ner.Org;
using EP.Ner.Uri;
using PravoRu.Common.Extensions;
using PravoRu.DataLake.Arbitr.Common.CaseNumbers;

namespace EP.Demo.Core
{
	public class MyAnalyzer : Analyzer
	{
		public const string ANALYZER_NAME = "MyAnalyzer";

		public override string Caption { get; } = "Мой анализатор";


		public override bool IsSpecific { get; } = true;

		public override string Name { get; } = nameof(MyAnalyzer);


		private readonly Processor _organizationProcessor;
		private readonly Processor _uriProcessor;
		private readonly Processor _dateProcessor;

		static TerminCollection _terminCollection;
		static TerminCollection _opfTerminCollection;

		public MyAnalyzer() : base()
		{
			_organizationProcessor = ProcessorService.CreateEmptyProcessor();
			_organizationProcessor.AddAnalyzer(new OrganizationAnalyzer());

			_uriProcessor = ProcessorService.CreateEmptyProcessor();
			_uriProcessor.AddAnalyzer(new UriAnalyzer());


			_dateProcessor = ProcessorService.CreateEmptyProcessor();
			_dateProcessor.AddAnalyzer(new DateAnalyzer());
		}

		public override Analyzer Clone()
		{
			return (Analyzer) new MyAnalyzer();
		}

		public static void Initialize()
		{
			_terminCollection = new TerminCollection();
			Termin tContract = new Termin("контракт", MorphLang.RU, true);
			tContract.AddAbridge("гос.контракт");
			tContract.AddAbridge("г.контракт");
			tContract.AddAbridge("гос.к-т");
			_terminCollection.Add(tContract);

			_opfTerminCollection = new TerminCollection();
			Termin tOpf = new Termin("ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСВЕННОСТЬЮ");
			tOpf.AddAbridge("ООО");
			tOpf.AddVariant("ЗАКРЫТОЕ АКЦИОНЕРНОЕ ОБЩЕСТВО");
			tOpf.AddAbridge("ЗАО");
			_opfTerminCollection.Add(tOpf);

			OrganizationAnalyzer.Initialize();
			UriAnalyzer.Initialize();
			DateAnalyzer.Initialize();
			ProcessorService.RegisterAnalyzer(new MyAnalyzer());
		}

		private Participant RecognizeParticipant(string text, int startOccurance, int endOccurance, OrganizationReferent organizationReferent)
		{
			var participant = new Participant
			{
				Name = organizationReferent.ToString()
			};
			var str = text.Substring(endOccurance,
				text.Length - endOccurance > 200
					? 200
					: (text.Length - endOccurance));
			var uriAnalysisResult = _uriProcessor.Process(new SourceOfAnalysis(str));
			if (uriAnalysisResult.Entities?.Count > 0)
			{
				participant.Inn = uriAnalysisResult.Entities.OfType<UriReferent>()
					.FirstOrDefault(x => x.Scheme == "ИНН")?.Value;
				participant.Ogrn = uriAnalysisResult.Entities.OfType<UriReferent>()
					.FirstOrDefault(x => x.Scheme == "ОГРН")?.Value;
			}
			else
			{
				str = text.Substring(startOccurance < 200 ? 0 : startOccurance - 200,
					200);
				uriAnalysisResult = _uriProcessor.Process(new SourceOfAnalysis(str));
				if (uriAnalysisResult.Entities?.Count > 0)
				{
					participant.Inn = uriAnalysisResult.Entities.OfType<UriReferent>()
						.FirstOrDefault(x => x.Scheme == "ИНН")?.Value;
					participant.Ogrn = uriAnalysisResult.Entities.OfType<UriReferent>()
						.FirstOrDefault(x => x.Scheme == "ОГРН")?.Value;
				}
			}

			return participant;
		}

		//
		// Summary:
		//     Основная функция выделения объектов
		//
		// Parameters:
		//   container:
		//
		//   lastStage:
		public override void Process(AnalysisKit kit)
		{
			try
			{
				List<Participant> organizationReferents = new List<Participant>();

				var analysisResult = _organizationProcessor.Process(kit.Sofa);
				var analyzerData = kit.GetAnalyzerData(this);

				//Ищем участников
				foreach (var organizationReferent in analysisResult.Entities.OfType<OrganizationReferent>().Where(x=>!x.ToString().ToUpper().Contains(" СУД ")))
				{
					var participant = new Participant()
					{
						Name = organizationReferent.ToString()
					};
					if (String.IsNullOrWhiteSpace(organizationReferent.INN) ||
					    String.IsNullOrWhiteSpace(organizationReferent.OGRN))
					{
						foreach (var occurance in organizationReferent.Occurrence)
						{
							var tempParticipant = RecognizeParticipant(kit.Sofa.Text, occurance.BeginChar, occurance.EndChar,
								organizationReferent);
							if (!tempParticipant.Inn.NullOrEmpty() && participant.Inn.NullOrEmpty())
							{
								participant.Inn = tempParticipant.Inn;
							}
							
							if (!tempParticipant.Ogrn.NullOrEmpty() && participant.Ogrn.NullOrEmpty())
							{
								participant.Ogrn = tempParticipant.Ogrn;
							}
						}
					}
					else
					{
						participant = new Participant
						{
							Inn = organizationReferent.INN,
							Ogrn = organizationReferent.OGRN,
							Name = organizationReferent.ToString()
						};
					}
					
					//Полученный участник может быть с такими же реквизатами, но с иным названием. Добавляем участника без реквизитов
					if (!organizationReferents.Any(x => x.Name.Equals(participant.Name)))
					{
						try
						{
							if (!participant.Inn.NullOrEmpty() && organizationReferents.Any(x => participant.Inn.Equals(x.Inn)))
							{
								participant.Inn = null;
							}
							if (!participant.Ogrn.NullOrEmpty() && organizationReferents.Any(x => participant.Ogrn.Equals(x.Ogrn)))
							{
								participant.Ogrn = null;
							}
						}
						catch (Exception e)
						{
							Console.WriteLine(e);
							throw;
						}
					}
					organizationReferents.Add(participant);
				}

				//Участники 
				var participants = organizationReferents.Distinct(new OrganizationComparator()).ToList();

				//Ищем контракты по датам
				List<Contract> contracts = new List<Contract>();

				analysisResult = _dateProcessor.Process(kit.Sofa);
				foreach (var dateReferent in analysisResult.Entities.OfType<DateReferent>())
				{
					if (dateReferent.Day > 0)
					{
						foreach (var occurance in dateReferent.Occurrence)
						{
							var start = occurance.BeginChar - 100 < 0 ? 0 : occurance.BeginChar - 100;
							var length = occurance.EndChar - occurance.BeginChar + 200;
							var str = "";
							try
							{
								str = kit.Sofa.Text.Substring(start,
									kit.Sofa.Text.Length - start >= length ? length : kit.Sofa.Text.Length - start);
							}
							catch (Exception e)
							{
								Console.WriteLine(e);
								throw;
							}
							
							AnalysisKit analyzisKit = new AnalysisKit(new SourceOfAnalysis(str), true, MorphLang.RU)
							{
								Processor = ProcessorService.CreateEmptyProcessor()
							};
							var numbers = ExtractionNumbers(analyzisKit.FirstToken).Distinct().ToList();
							if (numbers.Count > 0)
							{
								foreach (var number in numbers)
								{
									var contract = contracts.FirstOrDefault(x =>
										String.Equals(x.Number, number, StringComparison.InvariantCultureIgnoreCase));
									if (contract == null)
									{
										var c = new Contract()
										{
											Number = number
										};
										c.Dates.Add(dateReferent.Dt);
										contracts.Add(c);
									}
									else
									{
										contract.Dates.Add(dateReferent.Dt);
									}
								}

								break;
							}
						}
					}
				}


				//Пробуем выделить по слову контракт
				List<string> contractNumbers = new List<string>();
				for (Token t = kit.FirstToken; t != null; t = t.Next)
				{
					//Ищем контракты
					TerminToken token = _terminCollection.TryParse(t, TerminParseAttr.No);
					if (token != null)
					{
						var str = kit.Sofa.Text.Substring(token.EndChar + 1, 100);
						AnalysisKit analyzisKit = new AnalysisKit(new SourceOfAnalysis(str), true, MorphLang.RU)
						{
							Processor = ProcessorService.CreateEmptyProcessor()
						};
						var numbers = ExtractionNumbers(analyzisKit.FirstToken);
						if (numbers?.Count > 0)
						{
							contractNumbers.AddRange(numbers);
						}
					}
				}

				contractNumbers = contractNumbers.Distinct().ToList();
                contracts.AddRange(contractNumbers.Select(x => new Contract() {
                    Number = x,
                    Dates = new List<DateTime?>()
                }));
				//Просто вычленяем все номера
				var resultNumbers = ExtractionNumbers(kit.FirstToken);

				analyzerData.RegisterReferent(new MyReferent(nameof(MyReferent))
				{
					Contracts = contracts.Where(x=>CaseNumber.Parse(x.Number).IsValid == false).ToList(),
					Participants = participants
				});
				
				//Вывод в файл
				using (var streamWriter = new StreamWriter(Path.Combine("Results", GlobalState.File)))
				{
					streamWriter.WriteLine("Организации");
					foreach (var participant in participants)
					{
						streamWriter.WriteLine(
							$"INN:{participant.Inn}\t OGRN:{participant.Ogrn}\t {participant.Name}");
					}
					streamWriter.WriteLine();
					streamWriter.WriteLine("Контракты после слова 'контракт'");
					foreach (var contract in contractNumbers)
					{
						streamWriter.WriteLine($"{contract}");
					}
					streamWriter.WriteLine();
					streamWriter.WriteLine("Контракты рядом с датами ");
					foreach (var contract in contracts)
					{
						foreach (var date in contract.Dates)
						{
							streamWriter.WriteLine($"{contract.Number} {date}");
						}
					}
					streamWriter.WriteLine();
					streamWriter.WriteLine("Скорее всего нужные нам контракты (номера с датами входящие в номера по слову 'Контракт')".ToUpper());
					var cs = contracts.Where(x => contractNumbers.Contains(x.Number)).ToList();
					foreach (var contract in cs)
					{
						foreach (var date in contract.Dates)
						{
							streamWriter.WriteLine($"{contract.Number} {date}");
						}
					}
					streamWriter.WriteLine();
					streamWriter.WriteLine("Мусор");
					streamWriter.WriteLine("Номера");
					foreach (var contract in resultNumbers)
					{
						streamWriter.WriteLine($"{contract}");
					}
				}
				
			}
			catch (Exception e)
			{
				Console.WriteLine(e);
				throw;
			}
		}

		public List<String> ExtractionNumbers(Token token)
		{
			List<String> resultNumbers = new List<String>();
			for (Token t = token; t != null; t = t.Next)
			{
				if (MiscHelper.CheckNumberPrefix(t) != null)
				{
					var number = "";
					while (MiscHelper.CheckNumberPrefix(t = t.Next) != null)
					{
					} // Бывает ДЕЛО №А

					while (t != null)
					{
						if (t.IsWhitespaceAfter)
						{
							if (t.IsNumber || t.IsLetters ||
							    t.IsCharOf("АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя"))
							{
								number += t.GetSourceText();
							}

							if (t.IsChar('-'))
							{
								number += t.GetSourceText();
								t = t.Next;
								continue;
							}

							break;
						}
						else
						{
							if (t.IsCharOf("()"))
							{
								break;
							}

							number += t.GetSourceText();
						}


						t = t.Next;
					}

					if (number.Any(char.IsDigit))
					{
						resultNumbers.Add(number);
					}

					if (t == null)
					{
						break;
					}
				}
			}

			resultNumbers = resultNumbers.Distinct().ToList();
			return resultNumbers;
		}
	}
}