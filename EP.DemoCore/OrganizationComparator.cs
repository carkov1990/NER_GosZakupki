using System.Collections.Generic;

namespace EP.Demo.Core
{
	public class OrganizationComparator : IEqualityComparer<Participant>
	{
		public bool Equals(Participant x, Participant y)
		{
			return x.Inn.Equals(y.Inn) && x.Ogrn.Equals(y.Ogrn);
		}

		public int GetHashCode(Participant obj)
		{
			return obj.GetHashCode();
		}
	}
}